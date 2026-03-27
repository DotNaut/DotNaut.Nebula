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

-- ── Background worker: heartbeat table ──────────────────────────────
-- The worker (registered in _PG_init) inserts a row every ~10 seconds.
-- Requires: shared_preload_libraries = 'pg_dotnet' in postgresql.conf + restart.
CREATE TABLE IF NOT EXISTS heartbeat (
    id   SERIAL PRIMARY KEY,
    ts   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Check heartbeat records:
-- SELECT * FROM heartbeat ORDER BY id DESC LIMIT 10;
