-- ============================================================
-- PostgreSQL setup for Logical Replication Listener
-- Run this as a superuser (e.g. postgres)
-- ============================================================

-- 1. Make sure postgresql.conf has:
--      wal_level = logical
--      max_replication_slots = 4
--      max_wal_senders = 4
--    Then restart PostgreSQL.

-- 2. Create a replication user
CREATE ROLE repuser WITH REPLICATION LOGIN PASSWORD 'secret';

-- 3. Create a test database and grant access
CREATE DATABASE mydb OWNER repuser;

-- Connect to mydb:
\c mydb

-- 4. Create a sample table
CREATE TABLE IF NOT EXISTS orders (
    id          SERIAL PRIMARY KEY,
    customer    TEXT NOT NULL,
    amount      NUMERIC(10,2) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Grant permissions to repuser
GRANT ALL ON TABLE orders TO repuser;
GRANT USAGE, SELECT ON SEQUENCE orders_id_seq TO repuser;

-- 5. Create a publication for the table
CREATE PUBLICATION my_pub FOR TABLE orders;

-- 6. Create a replication slot
SELECT * FROM pg_create_logical_replication_slot('my_slot', 'pgoutput');

-- ============================================================
-- Test it: after starting the C# listener, run these:
-- ============================================================
--   INSERT INTO orders (customer, amount) VALUES ('Alice', 99.95);
--   INSERT INTO orders (customer, amount) VALUES ('Bob', 149.00);
--   UPDATE orders SET amount = 109.95 WHERE customer = 'Alice';
--   DELETE FROM orders WHERE customer = 'Bob';
