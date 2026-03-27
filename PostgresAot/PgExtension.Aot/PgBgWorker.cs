using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PgExtension.Aot;

/// <summary>
/// PostgreSQL BackgroundWorker registration struct (PG 18, x64).
/// Field order per bgworker.h: name, type, flags, start_time, restart_time,
/// library_name, function_name, main_arg, extra, notify_pid.
/// BGW_MAXLEN=96, MAXPGPATH=1024, BGW_EXTRALEN=128.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 1472)]
internal unsafe struct BackgroundWorker
{
    [FieldOffset(0)]    public fixed byte bgw_name[96];
    [FieldOffset(96)]   public fixed byte bgw_type[96];
    [FieldOffset(192)]  public int        bgw_flags;
    [FieldOffset(196)]  public int        bgw_start_time;
    [FieldOffset(200)]  public int        bgw_restart_time;
    [FieldOffset(204)]  public fixed byte bgw_library_name[1024];
    [FieldOffset(1228)] public fixed byte bgw_function_name[96];
    [FieldOffset(1328)] public nint       bgw_main_arg;   // 4 bytes padding before (Datum alignment)
    [FieldOffset(1336)] public fixed byte bgw_extra[128];
    [FieldOffset(1464)] public int        bgw_notify_pid;
}

/// <summary>
/// Background worker that inserts a row into public.heartbeat every second.
/// Registered in _PG_init, requires shared_preload_libraries = 'pg_dotnet'.
/// </summary>
internal static unsafe class PgBgWorker
{
    // ── Constants ────────────────────────────────────────────────────────
    const int BGWORKER_SHMEM_ACCESS               = 0x0001;
    const int BGWORKER_BACKEND_DATABASE_CONNECTION = 0x0002;
    const int BgWorkerStart_RecoveryFinished       = 2;

    const int WL_LATCH_SET        = 1 << 0;  // 1
    const int WL_TIMEOUT          = 1 << 3;  // 8
    const int WL_EXIT_ON_PM_DEATH = 1 << 5;  // 32
    const uint PG_WAIT_EXTENSION  = 0x07000000;

    const int SIGTERM = 15;
    const int LOG     = 15;  // PostgreSQL log level

    const int HeartbeatIntervalMs = 1_000;  // 1 second

    // ── Per-process state ───────────────────────────────────────────────
    static volatile int s_gotSigterm;
    static nint s_myLatch;

    // ═════════════════════════════════════════════════════════════════════
    //  PostgreSQL function imports (resolved via postgres.lib at link time)
    // ═════════════════════════════════════════════════════════════════════

    [DllImport("postgres")] static extern void RegisterBackgroundWorker(BackgroundWorker* worker);
    [DllImport("postgres")] static extern void BackgroundWorkerInitializeConnection(byte* dbname, byte* username, uint flags);
    [DllImport("postgres")] static extern void BackgroundWorkerUnblockSignals();

    [DllImport("postgres")] static extern int  WaitLatch(nint latch, int wakeEvents, int timeout, uint waitEventInfo);
    [DllImport("postgres")] static extern void ResetLatch(nint latch);
    [DllImport("postgres")] static extern void SetLatch(nint latch);
    [DllImport("postgres")] static extern nint pqsignal_be(int signo, nint handler);

    [DllImport("postgres")] static extern void SetCurrentStatementStartTimestamp();
    [DllImport("postgres")] static extern void StartTransactionCommand();
    [DllImport("postgres")] static extern void CommitTransactionCommand();
    [DllImport("postgres")] static extern nint GetTransactionSnapshot();
    [DllImport("postgres")] static extern void PushActiveSnapshot(nint snapshot);
    [DllImport("postgres")] static extern void PopActiveSnapshot();

    [DllImport("postgres")] static extern int  SPI_connect();
    [DllImport("postgres")] static extern int  SPI_execute(byte* src, byte readOnly, int tcount);
    [DllImport("postgres")] static extern int  SPI_finish();

    [DllImport("postgres")] static extern void proc_exit(int code);

    // Error reporting (LOG-level only; format string must have no % specifiers)
    [DllImport("postgres")] static extern byte errstart(int elevel, byte* domain);
    [DllImport("postgres")] static extern int  errmsg_internal(byte* fmt);
    [DllImport("postgres")] static extern void errfinish(byte* filename, int lineno, byte* funcname);

    // Win32 — resolve PGDLLIMPORT globals from postgres.exe at runtime
    [DllImport("kernel32")] static extern nint GetModuleHandleA(nint moduleName);
    [DllImport("kernel32")] static extern nint GetProcAddress(nint hModule, byte* procName);

    // ═════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Copy a UTF-8 span into a fixed-size null-terminated buffer.</summary>
    static void CopyUtf8(byte* dest, int destSize, ReadOnlySpan<byte> src)
    {
        int len = Math.Min(src.Length, destSize - 1);
        fixed (byte* p = src)
            for (int i = 0; i < len; i++)
                dest[i] = p[i];
        dest[len] = 0;
    }

    /// <summary>Resolve address of a global variable exported from postgres.exe.</summary>
    static nint ResolveGlobal(ReadOnlySpan<byte> name)
    {
        nint hModule = GetModuleHandleA(0);
        fixed (byte* p = name)
            return GetProcAddress(hModule, p);
    }

    /// <summary>Read Latch *MyLatch from the postgres.exe data segment.</summary>
    static nint ResolveMyLatch()
    {
        nint addr = ResolveGlobal("MyLatch"u8);
        if (addr == 0) return 0;
        return *(nint*)addr;   // dereference Latch** -> Latch*
    }

    // ── Logging ───────────────────────────────────────────────────────

    /// <summary>Emit a LOG-level message to the PostgreSQL server log.</summary>
    static void Log(ReadOnlySpan<byte> message)
    {
        fixed (byte* domain = "pg_dotnet"u8)
        fixed (byte* msg = message)
        fixed (byte* file = "PgBgWorker.cs"u8)
        fixed (byte* func = "Log"u8)
        {
            if (errstart(LOG, domain) != 0)
            {
                errmsg_internal(msg);
                errfinish(file, 0, func);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Signal handler
    // ═════════════════════════════════════════════════════════════════════

    [UnmanagedCallersOnly]
    static void OnSigterm(int sig)
    {
        s_gotSigterm = 1;
        if (s_myLatch != 0)
            SetLatch(s_myLatch);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Exported entry points
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by PostgreSQL when pg_dotnet is loaded via shared_preload_libraries.
    /// Registers the heartbeat background worker.
    /// Only effective during postmaster startup (guarded by process_shared_preload_libraries_in_progress).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "_PG_init")]
    public static void PgInit()
    {
        BackgroundWorker worker = default;

        CopyUtf8(worker.bgw_name,          96,   "pg_dotnet heartbeat"u8);
        CopyUtf8(worker.bgw_type,          96,   "pg_dotnet heartbeat"u8);
        CopyUtf8(worker.bgw_library_name,  1024, "pg_dotnet"u8);
        CopyUtf8(worker.bgw_function_name, 96,   "pg_dotnet_heartbeat_main"u8);

        worker.bgw_flags        = BGWORKER_SHMEM_ACCESS | BGWORKER_BACKEND_DATABASE_CONNECTION;
        worker.bgw_start_time   = BgWorkerStart_RecoveryFinished;
        worker.bgw_restart_time = 10;
        worker.bgw_main_arg     = 0;
        worker.bgw_notify_pid   = 0;

        RegisterBackgroundWorker(&worker);
    }

    /// <summary>
    /// Main loop of the heartbeat background worker.
    /// Inserts a row into public.heartbeat every ~1 second.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "pg_dotnet_heartbeat_main")]
    public static void HeartbeatMain(nint mainArg)
    {
        Log("pg_dotnet: HeartbeatMain entered"u8);

        // Signal handling
        delegate* unmanaged<int, void> handler = &OnSigterm;
        pqsignal_be(SIGTERM, (nint)handler);
        BackgroundWorkerUnblockSignals();
        Log("pg_dotnet: signals configured"u8);

        // Cache latch pointer
        s_myLatch = ResolveMyLatch();
        Log("pg_dotnet: latch resolved"u8);

        // Connect to the "postgres" database
        fixed (byte* db = "postgres"u8)
            BackgroundWorkerInitializeConnection(db, null, 0);
        Log("pg_dotnet: connected to database"u8);

        // Ensure heartbeat table exists
        SetCurrentStatementStartTimestamp();
        StartTransactionCommand();
        SPI_connect();
        PushActiveSnapshot(GetTransactionSnapshot());

        fixed (byte* ddl = "CREATE TABLE IF NOT EXISTS heartbeat (id SERIAL PRIMARY KEY, ts TIMESTAMPTZ NOT NULL DEFAULT now())"u8)
            SPI_execute(ddl, 0, 0);

        SPI_finish();
        PopActiveSnapshot();
        CommitTransactionCommand();

        Log("pg_dotnet: heartbeat table ensured, entering main loop"u8);

        // Main loop
        while (s_gotSigterm == 0)
        {
            WaitLatch(
                s_myLatch,
                WL_LATCH_SET | WL_TIMEOUT | WL_EXIT_ON_PM_DEATH,
                HeartbeatIntervalMs,
                PG_WAIT_EXTENSION);

            ResetLatch(s_myLatch);

            if (s_gotSigterm != 0)
                break;

            // Insert heartbeat row inside a transaction
            SetCurrentStatementStartTimestamp();
            StartTransactionCommand();
            SPI_connect();
            PushActiveSnapshot(GetTransactionSnapshot());

            fixed (byte* sql = "INSERT INTO heartbeat (ts) VALUES (now())"u8)
                SPI_execute(sql, 0, 0);   // read_only=false, tcount=0

            SPI_finish();
            PopActiveSnapshot();
            CommitTransactionCommand();
        }

        Log("pg_dotnet: heartbeat worker stopping"u8);
        proc_exit(0);
    }
}
