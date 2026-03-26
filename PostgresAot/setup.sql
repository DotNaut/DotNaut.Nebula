-- Drop if exists (for re-running)
DROP FUNCTION IF EXISTS csharp_add(integer, integer);

-- Register the C# NativeAOT function
-- 'pg_dotnet' = DLL name (without extension)
-- 'pg_add' = exported C function name
CREATE FUNCTION csharp_add(integer, integer) RETURNS integer
    AS 'pg_dotnet', 'pg_add'
    LANGUAGE C STRICT;

-- Tests
SELECT csharp_add(2, 3) AS "expect_5";
SELECT csharp_add(0, 0) AS "expect_0";
SELECT csharp_add(-10, 7) AS "expect_-3";
SELECT csharp_add(2147483600, 47) AS "expect_2147483647";
