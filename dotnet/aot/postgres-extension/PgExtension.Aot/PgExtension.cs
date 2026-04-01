using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PgExtension.Aot;

/// <summary>
/// PostgreSQL 18 Pg_magic_struct (72 bytes on x64).
/// Layout matches fmgr.h: int len + Pg_abi_values(int version, int funcmaxargs,
/// int indexmaxkeys, int namedatalen, int float8byval, char[32] abi_extra)
/// + const char* name + const char* version.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
internal unsafe struct Pg_magic_struct
{
    [FieldOffset(0)]  public int Len;
    [FieldOffset(4)]  public int Version;
    [FieldOffset(8)]  public int FuncMaxArgs;
    [FieldOffset(12)] public int IndexMaxKeys;
    [FieldOffset(16)] public int NameDataLen;
    [FieldOffset(20)] public int Float8ByVal;
    [FieldOffset(24)] public fixed byte AbiExtra[32];
    [FieldOffset(56)] public nint Name;
    [FieldOffset(64)] public nint VersionStr;
}

/// <summary>
/// PostgreSQL Pg_finfo_record: { int api_version }.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Pg_finfo_record
{
    public int ApiVersion;
}

/// <summary>
/// PostgreSQL NullableDatum (16 bytes on x64): { Datum value; bool isnull; }.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct NullableDatum
{
    [FieldOffset(0)] public nint Value;
    [FieldOffset(8)] public byte IsNull;
}

/// <summary>
/// PostgreSQL FunctionCallInfoBaseData fixed part (32 bytes on x64).
/// args[] flexible array follows immediately at offset 32.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct FunctionCallInfoBaseData
{
    [FieldOffset(0)]  public nint Flinfo;
    [FieldOffset(8)]  public nint Context;
    [FieldOffset(16)] public nint ResultInfo;
    [FieldOffset(24)] public uint FnCollation;
    [FieldOffset(28)] public byte IsNull;
    [FieldOffset(30)] public short NArgs;
}

internal static class PgExports
{
    // PG 18.2 constants (from C:\Program Files\PostgreSQL\18\include\)
    private const int PgMagicLen = 72;
    private const int PgVersion = 1800;      // PG_VERSION_NUM (180002) / 100
    private const int FuncMaxArgs = 100;
    private const int IndexMaxKeys = 32;
    private const int NameDataLen = 64;
    private const int Float8ByVal = 1;

    private static Pg_magic_struct s_magic;
    private static Pg_finfo_record s_finfo;
    private static bool s_magicInitialized;

    private static unsafe void EnsureMagicInitialized()
    {
        if (s_magicInitialized) return;

        s_magic.Len = PgMagicLen;
        s_magic.Version = PgVersion;
        s_magic.FuncMaxArgs = FuncMaxArgs;
        s_magic.IndexMaxKeys = IndexMaxKeys;
        s_magic.NameDataLen = NameDataLen;
        s_magic.Float8ByVal = Float8ByVal;
        s_magic.Name = 0;
        s_magic.VersionStr = 0;

        // FMGR_ABI_EXTRA = "PostgreSQL" (10 chars + null, rest zeroed by static init)
        fixed (byte* p = s_magic.AbiExtra)
            "PostgreSQL"u8.CopyTo(new Span<byte>(p, 32));

        s_finfo.ApiVersion = 1;
        s_magicInitialized = true;
    }

    /// <summary>
    /// PG_MODULE_MAGIC: called by PostgreSQL to verify module compatibility.
    /// Must return a pointer to Pg_magic_struct that persists after the call.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Pg_magic_func")]
    public static unsafe Pg_magic_struct* PgMagicFunc()
    {
        EnsureMagicInitialized();
        return (Pg_magic_struct*)Unsafe.AsPointer(ref s_magic);
    }

    /// <summary>
    /// PG_FUNCTION_INFO_V1(pg_add): tells PostgreSQL that pg_add uses
    /// version-1 calling convention.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "pg_finfo_pg_add")]
    public static unsafe Pg_finfo_record* PgFinfoAdd()
    {
        EnsureMagicInitialized();
        return (Pg_finfo_record*)Unsafe.AsPointer(ref s_finfo);
    }

    /// <summary>
    /// The actual function: pg_add(int4, int4) -> int4.
    /// Uses PostgreSQL version-1 calling convention.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "pg_add")]
    public static unsafe nint PgAdd(nint fcinfo)
    {
        var info = (FunctionCallInfoBaseData*)fcinfo;
        info->IsNull = 0; // result is not null

        // args[] starts at offset 32 (right after FunctionCallInfoBaseData fixed fields)
        // Each NullableDatum is 16 bytes
        var args = (NullableDatum*)(fcinfo + 32);

        int a = (int)args[0].Value;
        int b = (int)args[1].Value;

        return (nint)(a + b);
    }
}
