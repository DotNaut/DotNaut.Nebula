using System;
using System.Runtime.InteropServices;

// PostgreSQL 18 Pg_magic_struct (72 bytes on x64)
// Fields are sequential: int len, int version, int funcmaxargs, int indexmaxkeys,
// int namedatalen, int float8byval, byte[32] abi_extra, nint name, nint version_str
[StructLayout(LayoutKind.Sequential)]
unsafe struct Pg_magic_struct
{
    public int Len;
    public int Version;
    public int FuncMaxArgs;
    public int IndexMaxKeys;
    public int NameDataLen;
    public int Float8ByVal;
    public fixed byte AbiExtra[32];
    public nint Name;
    public nint VersionStr;
}

[StructLayout(LayoutKind.Sequential)]
struct Pg_finfo_record
{
    public int ApiVersion;
}

// NullableDatum: Datum (nint) + isnull (byte) + padding = 16 bytes on x64
[StructLayout(LayoutKind.Sequential)]
struct NullableDatum
{
    public nint Value;
    public byte IsNull;
    // 7 bytes padding implicit
}

// FunctionCallInfoBaseData fixed part: 32 bytes on x64
[StructLayout(LayoutKind.Sequential)]
struct FunctionCallInfoBaseData
{
    public nint Flinfo;
    public nint Context;
    public nint ResultInfo;
    public uint FnCollation;
    public byte IsNull;
    byte _pad;
    public short NArgs;
    // args[] follows at offset 32
}

static unsafe class PgExports
{
    static Pg_magic_struct s_magic;
    static Pg_finfo_record s_finfo;
    static bool s_initialized;

    static void EnsureInitialized()
    {
        if (s_initialized) return;

        s_magic.Len = 72;
        s_magic.Version = 1800;
        s_magic.FuncMaxArgs = 100;
        s_magic.IndexMaxKeys = 32;
        s_magic.NameDataLen = 64;
        s_magic.Float8ByVal = 1;
        s_magic.Name = 0;
        s_magic.VersionStr = 0;

        s_magic.AbiExtra[0] = (byte)'P';
        s_magic.AbiExtra[1] = (byte)'o';
        s_magic.AbiExtra[2] = (byte)'s';
        s_magic.AbiExtra[3] = (byte)'t';
        s_magic.AbiExtra[4] = (byte)'g';
        s_magic.AbiExtra[5] = (byte)'r';
        s_magic.AbiExtra[6] = (byte)'e';
        s_magic.AbiExtra[7] = (byte)'S';
        s_magic.AbiExtra[8] = (byte)'Q';
        s_magic.AbiExtra[9] = (byte)'L';

        s_finfo.ApiVersion = 1;
        s_initialized = true;
    }

    [UnmanagedCallersOnly(EntryPoint = "Pg_magic_func")]
    static Pg_magic_struct* PgMagicFunc()
    {
        EnsureInitialized();
        fixed (Pg_magic_struct* p = &s_magic)
            return p;
    }

    [UnmanagedCallersOnly(EntryPoint = "pg_finfo_pg_add")]
    static Pg_finfo_record* PgFinfoAdd()
    {
        EnsureInitialized();
        fixed (Pg_finfo_record* p = &s_finfo)
            return p;
    }

    [UnmanagedCallersOnly(EntryPoint = "pg_add")]
    static nint PgAdd(nint fcinfo)
    {
        var info = (FunctionCallInfoBaseData*)fcinfo;
        info->IsNull = 0;

        var args = (NullableDatum*)(fcinfo + 32);
        int a = (int)args[0].Value;
        int b = (int)args[1].Value;

        return (nint)(a + b);
    }
}
