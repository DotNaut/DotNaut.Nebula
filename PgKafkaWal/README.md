# PgKafkaWal — PostgreSQL Replication as a Kafka Alternative

Kafka and PostgreSQL logical replication solve the same fundamental problem — delivering an ordered stream of events from producers to consumers — but they arrive at it from opposite directions.

Kafka built a distributed commit log from scratch. PostgreSQL already had one: the **WAL (Write-Ahead Log)**. Every INSERT, UPDATE, DELETE is already written to an append-only, ordered, durable log — the same properties that make Kafka work.

## Kafka vs PostgreSQL: Parallel Concepts

| Kafka | PostgreSQL | Role |
|-------|-----------|------|
| Topic | Publication | What to stream |
| Partition | Publication + Slot (per table partition) | Parallelism unit |
| Consumer Group / Offset | Replication Slot / LSN | Read position bookmark |
| Broker log | WAL | Append-only durable log |
| `acks=all` | `synchronous_commit=on` | Durability guarantee |
| `auto.commit.interval.ms` | Npgsql feedback interval (~10s) | Ack batching |
| `retention.ms` | Partition drop / slot advance | Data lifecycle |
| Schema Registry | `RelationMessage` (automatic) | Schema evolution |

This experiment has two goals:

1. **When can PostgreSQL replace Kafka?** For moderate-scale streaming, PG replication may eliminate the need for a separate message broker — zero infrastructure overhead, typed data, SQL on the same tables.
2. **Understanding the primitives for a custom Data Platform.** The patterns behind WAL, replication slots, and publications are not unique to PostgreSQL or Kafka — they are fundamental building blocks of any event streaming system. Studying them side by side builds the foundation for designing our own.

## How It Works

```
PostgreSQL WAL  --->  pgoutput plugin  --->  Replication Slot  --->  C# (Npgsql)
```

1. **WAL (Write-Ahead Log)** records every change to the database
2. **Logical Decoding** decodes binary WAL into structured change events
3. **Publication** defines which tables to track
4. **Replication Slot** bookmarks how far the consumer has read (so nothing is lost on disconnect)
5. **pgoutput** is the built-in output plugin (protocol used by native logical replication)
6. **Npgsql** connects using the streaming replication protocol and receives messages as `IAsyncEnumerable`

## PostgreSQL Setup

### 1. Enable Logical Replication

In `postgresql.conf`:

```ini
wal_level = logical          # required (default is 'replica')
max_replication_slots = 4    # at least 1 per listener
max_wal_senders = 4          # at least 1 per listener
```

Restart PostgreSQL after changing `wal_level`.

### 2. Create User with Replication Permission

```sql
CREATE ROLE repuser WITH REPLICATION LOGIN PASSWORD 'secret';
```

### 3. Allow Replication Connections

In `pg_hba.conf` (if connecting remotely):

```
host    replication    repuser    0.0.0.0/0    md5
```

### 4. Create Database and Table

```sql
CREATE DATABASE mydb OWNER repuser;

\c mydb

CREATE TABLE orders (
    id          SERIAL PRIMARY KEY,
    customer    TEXT NOT NULL,
    amount      NUMERIC(10,2) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

GRANT ALL ON TABLE orders TO repuser;
GRANT USAGE, SELECT ON SEQUENCE orders_id_seq TO repuser;
```

### 5. Create Publication and Replication Slot

```sql
-- Publication defines WHICH tables to replicate
CREATE PUBLICATION my_pub FOR TABLE orders;

-- Slot bookmarks the WAL read position (run separately, not in a transaction with DDL)
SELECT * FROM pg_create_logical_replication_slot('my_slot', 'pgoutput');
```

> The slot creation **must** be a separate statement, not in the same transaction as DDL (CREATE TABLE, etc).

Or use `FOR ALL TABLES` to track everything:

```sql
CREATE PUBLICATION my_pub FOR ALL TABLES;
```

Full setup script: [setup.sql](PgKafkaWal.Consumer/setup.sql)

## Running the Listener

```bash
cd PgKafkaWal.Consumer
dotnet run
```

Output:

```
PostgreSQL Logical Replication Listener
Press Ctrl+C to stop.

Connected. Starting replication stream...

--- BEGIN (xid=778) ---
  INSERT public.orders
    id = 1
    customer = Alice
    amount = 99.95
    created_at = 2026-02-21 15:10:26.440134+02
--- COMMIT ---

--- BEGIN (xid=780) ---
  UPDATE public.orders
    id = 1
    customer = Alice
    amount = 109.95
    created_at = 2026-02-21 15:10:26.440134+02
--- COMMIT ---

--- BEGIN (xid=781) ---
  DELETE public.orders (by key)
    id = 2
--- COMMIT ---
```

### Acknowledgment and Batching

WAL position acknowledgment in PostgreSQL replication is **already batched** by design — analogous to Kafka's `enable.auto.commit=true`. Npgsql sends actual feedback to the server automatically (~10s interval, or on server request), so the consumer processes messages at full speed without network round-trips.

For controlled acknowledgment, call `SetReplicationStatus` selectively (e.g. every N messages). On crash, PostgreSQL re-delivers from the last confirmed position — same as Kafka's manual `commitSync()`.

`SetReplicationStatus(message.WalEnd)` is a **local in-memory update** — no network call. `SendStatusUpdate()` forces an immediate round-trip — see [Acknowledgment Mode benchmarks](#acknowledgment-mode-impact) for the performance impact.

| Kafka | PostgreSQL Replication |
|-------|----------------------|
| `enable.auto.commit=true` | `SetReplicationStatus` per message (Npgsql batches feedback) |
| `enable.auto.commit=false` + `commitSync()` | Selective `SetReplicationStatus` + `SendStatusUpdate()` |
| `acks=all` / `acks=1` | `synchronous_commit = on` / `off` |
| `linger.ms` | `commit_delay` (WAL flush grouping) |

#### Write-Side Tuning

Key knob: `synchronous_commit = off` skips WAL fsync wait (~2-3x write speedup, risk: lose last ~200ms on crash). This is comparable to Kafka's `acks=1`. Can be set per-session: `SET synchronous_commit = off;`. See also `commit_delay` for grouping nearby commits into one flush.

## Replication Slot Behavior

### Guaranteed Delivery

When a consumer disconnects, PostgreSQL **retains WAL** from the slot's last confirmed position. On reconnect, all missed changes are delivered immediately. Nothing is lost.

### Slot Management

```sql
-- View slot position
SELECT slot_name, active, confirmed_flush_lsn FROM pg_replication_slots;

-- Advance slot forward (skip messages)
SELECT pg_replication_slot_advance('my_slot', '0/1E00000');

-- Drop abandoned slot (frees WAL)
SELECT pg_drop_replication_slot('my_slot');
```

> **Warning:** While a slot exists and no consumer is reading, WAL files accumulate on disk. Always drop slots that are no longer needed.

### Retention Window Pattern

PostgreSQL replication slots can only advance **forward**. There's no built-in "rewind" or "seek to offset" like Kafka. However, you can implement a retention window using an anchor slot and `pg_copy_logical_replication_slot`:

```
Anchor slot (retention_anchor)           Consumer slot (my_slot)
     │                                        │
     ▼                                        ▼
─────┬────────────────────────────────────────┬──────────── WAL ──>
     │◄──── retention window (WAL retained) ──►│
     │                                        │
     advance periodically                     consumer reads here
     (e.g. keep last 1 hour)
```

**Setup:**

```sql
-- 1. Create an anchor slot (holds the WAL retention boundary)
SELECT * FROM pg_create_logical_replication_slot('retention_anchor', 'pgoutput');

-- 2. Create a consumer slot (used by the application)
SELECT * FROM pg_create_logical_replication_slot('my_slot', 'pgoutput');

-- 3. Optionally track LSN markers with timestamps
CREATE TABLE lsn_markers (
    ts TIMESTAMPTZ PRIMARY KEY DEFAULT now(),
    lsn PG_LSN NOT NULL DEFAULT pg_current_wal_lsn()
);
```

**Periodic maintenance** (advance anchor to keep a rolling window):

```sql
-- Record current LSN marker
INSERT INTO lsn_markers DEFAULT VALUES;

-- Advance anchor to position from 1 hour ago (frees older WAL)
SELECT pg_replication_slot_advance(
    'retention_anchor',
    (SELECT lsn FROM lsn_markers WHERE ts < now() - interval '1 hour' ORDER BY ts DESC LIMIT 1)
);

-- Clean up old markers
DELETE FROM lsn_markers WHERE ts < now() - interval '2 hours';
```

**Rewind a consumer** (replay from an earlier point within the retention window):

```sql
-- 1. Drop the current consumer slot
SELECT pg_drop_replication_slot('my_slot');

-- 2. Copy the anchor slot (creates a new slot at the anchor's position)
SELECT pg_copy_logical_replication_slot('retention_anchor', 'my_slot');

-- 3. Optionally advance to a specific point within the window
SELECT pg_replication_slot_advance(
    'my_slot',
    (SELECT lsn FROM lsn_markers WHERE ts >= now() - interval '30 minutes' ORDER BY ts LIMIT 1)
);
```

The consumer reconnects and receives all changes from the new position. This provides Kafka-like "seek to timestamp" capability within the retention window.

> **Note:** `pg_copy_logical_replication_slot` requires PostgreSQL 12+.

## Partitioned Replication

PostgreSQL allows subscribing to **individual partitions**, enabling true read sharding.

```
                      bench_part (partitioned by hash)
                       ┌──────┴──────┐
                  bench_part_0   bench_part_1
                       |              |
                  pub_part_0     pub_part_1        pub_part_all
                       |              |               |
                  slot_part_0    slot_part_1      slot_part_all
                       |              |               |
                  Reader 0       Reader 1         Reader ALL
                  (region 1,2)   (region 3-6)     (all data)
```

### Per-Partition Publication

```sql
-- Partitioned table
CREATE TABLE bench_part (...) PARTITION BY HASH (region);
CREATE TABLE bench_part_0 PARTITION OF bench_part FOR VALUES WITH (MODULUS 2, REMAINDER 0);
CREATE TABLE bench_part_1 PARTITION OF bench_part FOR VALUES WITH (MODULUS 2, REMAINDER 1);

-- One publication per partition = each consumer sees only its shard
CREATE PUBLICATION pub_part_0 FOR TABLE bench_part_0;
CREATE PUBLICATION pub_part_1 FOR TABLE bench_part_1;

-- Or one publication for the whole table (all data, table name = root)
CREATE PUBLICATION pub_all FOR TABLE bench_part WITH (publish_via_partition_root = true);
```

Each consumer subscribes to its own publication/slot and receives only its partition's data. No duplication, true parallel processing.

## Benchmark Results

Benchmarks run on a single machine (PostgreSQL 18, .NET 10, Npgsql 10.0.1, Windows 11). Writer and reader compete for the same CPU/IO.

### Running the Benchmark

```bash
cd PgKafkaWal.Benchmark

# Single reader
dotnet run -- --messages 10000 --writers 4 --batch-size 100

# Multiple readers (duplicate mode — each reads all data)
dotnet run -- --messages 10000 --writers 4 --readers 4 --batch-size 100

# Partitioned mode (each reader reads only its partition)
dotnet run -- --messages 10000 --writers 4 --readers 4 --batch-size 100 --partitioned

# Sync ack (network round-trip per ack)
dotnet run -- --messages 10000 --writers 4 --batch-size 100 --sync-ack

# Sync ack every 1000 messages
dotnet run -- --messages 10000 --writers 4 --batch-size 100 --ack-interval 1000 --sync-ack
```

### Single Reader Throughput

| Messages | Writers | Batch | Write msg/s | E2E msg/s | P50 Latency |
|----------|---------|-------|-------------|-----------|-------------|
| 1,000 | 2 | 50 | 31,289 | 30,909 | 2.9 ms |
| 10,000 | 4 | 100 | 150,513 | 108,652 | 15.3 ms |
| 50,000 | 8 | 200 | 254,289 | 88,679 | 251 ms |
| 100,000 | 8 | 500 | 238,107 | 124,078 | 239 ms |

Single replication stream maxes out at ~90-125k msg/s read throughput. When writers outpace the reader, latency grows as messages queue in WAL. First run after PG restart may show higher latency due to cold start (replication connection setup).

### Multiple Readers — Duplicate Mode (same data to all)

10,000 messages, 4 writers, batch 100:

| Readers | E2E per reader | Combined delivery | P50 Latency |
|---------|---------------|-------------------|-------------|
| 1 | 108,652 msg/s | 108,652 msg/s | 15 ms |
| 2 | 105,818 msg/s | 211,635 msg/s | 16-20 ms |
| 4 | 64,316 msg/s | 257,262 msg/s | 13-21 ms |
| 8 | 48,814 msg/s | 390,509 msg/s | 42-69 ms |

Each additional slot = one more WAL sender process. Up to 2 slots scale nearly linearly. At 4+, write throughput drops as WAL senders compete with writers for CPU/IO on the same host.

### Multiple Readers — Partitioned Mode (each reads own shard)

10,000 messages, 4 writers, batch 100:

| Readers | E2E throughput | P50 Latency | Read catchup |
|---------|---------------|-------------|-------------|
| 2 partitions | 102,945 msg/s | 5-7 ms | 2 ms |
| 4 partitions | 112,447 msg/s | 2-3 ms | 0 ms |

50,000 messages, 4 writers, batch 200:

| Readers | E2E throughput | P50 Latency | Read catchup |
|---------|---------------|-------------|-------------|
| 2 partitions | 207,789 msg/s | 21-23 ms | 34 ms |
| 4 partitions | 178,972 msg/s | 3-4 ms | 0 ms |

**Key finding:** Partitioning dramatically reduces latency — P50 drops from **251ms (single reader at 50k) to 3-4ms** with 4 partitions. Each reader processes fewer messages and keeps up with writers in real-time (catchup = 0 ms).

### Acknowledgment Mode Impact

`SetReplicationStatus()` is a local in-memory update — Npgsql sends actual `StandbyStatusUpdate` to the server automatically every ~10 seconds. `SendStatusUpdate()` forces an immediate network round-trip.

10,000 messages, 4 writers, batch 100 (localhost):

| Ack Mode | E2E throughput | P50 Latency | Read catchup |
|----------|---------------|-------------|-------------|
| Local ack every 1 msg (default) | 108,652 msg/s | 15.3 ms | 26 ms |
| **Sync ack every 1 msg** | 78,452 msg/s | 37.9 ms | 59 ms |
| Sync ack every 1000 msgs | 117,229 msg/s | 11.4 ms | 20 ms |

Sync ack on every message reduces throughput by **~28%** and increases P50 latency by **~2.5x** on localhost. Batching sync ack every 1000 messages actually **outperforms** per-message local ack — fewer `SetReplicationStatus` calls = less overhead in the reader loop.

> **Note:** All benchmarks used default local ack unless noted. Npgsql batches actual feedback sends (~10s interval), so in sub-second benchmarks the server likely received **zero** feedback messages — the most favorable condition for throughput.

## Comparison with Kafka

> **Important caveat:** Kafka's headline throughput numbers (1M+ msg/s) are achieved with **large batches** (`linger.ms=50-200`, `batch.size=64KB+`, `acks=1`). With per-message synchronous commit (`acks=all`, no batching), Kafka typically delivers **1-5k msg/s** — significantly slower than PostgreSQL replication in comparable mode.

### Throughput by Commit Mode

| Mode | PG Replication | Kafka |
|------|---------------|-------|
| **Per-message sync** (safest) | 5-20k msg/s | 1-5k msg/s |
| **Batched / async** (fastest) | 100-195k msg/s | 200k-1M+ msg/s |
| **Gap** | ~1x baseline | ~100-200x vs sync mode |

PostgreSQL scales more linearly because each SQL COMMIT is already a durable write. Kafka's per-message mode is slow because each `produce()` + `acks=all` requires a full round-trip to all ISR replicas.

### Feature Comparison

| | PG Logical Replication | Apache Kafka |
|-|----------------------|-------------|
| **Write throughput** | 100-195k msg/s (batched INSERTs) | 200k-1M+ msg/s (batched, async) |
| **Read throughput** | 60-90k msg/s (single stream) | 300k-1M+ msg/s (batched fetch) |
| **Latency** | 3-35 ms P50 (partitioned) | 2-5 ms P50 (batched) |
| **Partitioning** | Via table partitioning | Native topic partitions |
| **Consumer groups** | Manual (slot per consumer) | Built-in with auto-rebalance |
| **Replay** | Retention window via anchor slot | Arbitrary offset seek |
| **Retention** | WAL grows while slot exists | Configurable TTL/size |
| **Infrastructure** | Zero (built into PG) | Brokers + KRaft/ZooKeeper |
| **Data format** | Typed columns with schema | Typically JSON (+ Schema Registry) |
| **Schema** | Automatic (RelationMessage) | Requires Schema Registry |
| **Query on same data** | Full SQL with indexes | Separate DB needed |
| **Transaction boundaries** | Native (BEGIN/COMMIT) | Requires transactional producer |
| **Ack batching** | Automatic (~10s feedback interval) | Configurable (auto-commit / manual) |

### Advantages of PG Replication

- **Zero infrastructure** — no separate cluster, built into the database
- **Structured typed data** — columns arrive with types and names, no JSON serialization overhead
- **Same data is queryable** — SELECT with indexes, JOINs, aggregations on the same tables being replicated
- **Transactional semantics** — events arrive in COMMIT order with transaction boundaries
- **Guaranteed delivery** — slot retains WAL until consumer acknowledges
- **Consistent per-message performance** — no dramatic throughput cliff when switching from batch to per-message mode

### Advantages of Kafka

- **Higher peak throughput** — optimized for append-only log, zero-copy, batched fetch (but only in batch mode)
- **Horizontal scaling** — add brokers and partitions linearly
- **Consumer groups** — automatic rebalance, offset commit, multiple independent groups per topic
- **Native retention and replay** — re-read data from any point; PG requires anchor slot pattern
- **Backpressure** — slow consumer is fine, data accumulates in Kafka. Slow PG consumer bloats WAL on disk

### When to Use What

- **PG Replication**: CDC from PostgreSQL, 1-4 consumers, <100k events/sec, no extra infrastructure wanted, need SQL access to the same data, per-message durability matters
- **Kafka**: Event-driven architecture, many consumer groups, >100k events/sec, need native replay/retention, batch processing acceptable
- **Both (PG -> Debezium -> Kafka)**: Best of both worlds — PG handles transactional CDC, Kafka handles fan-out, retention, and consumer scaling

## Alternatives

| Approach | Pros | Cons |
|----------|------|------|
| **Npgsql Logical Replication** (this project) | Native .NET, no middleware, low latency, typed data | Manage slot lifecycle, single-server WAL |
| **Debezium** | Full CDC platform, Kafka integration, schema history | JVM, Kafka required, heavier infrastructure |
| **LISTEN/NOTIFY + triggers** | Simple, no wal_level change | Manual triggers, payload 8KB limit, not guaranteed |
| **Polling with timestamps** | Simplest to implement | Latency, missed deletes, load on DB |

## Roadmap

1. **Kafka benchmark on the same host** — run an actual Kafka instance, implement an equivalent producer/consumer in C#, and compare throughput and latency against PG replication under identical conditions.
2. **Abstract the streaming pattern** — extract a common interface (publish/subscribe/ack) that works over both PG replication and Kafka, usable in .NET applications.
3. **Advanced patterns** — research what PostgreSQL offers beyond Kafka: publication row filters (PG 15+), indexed queries on streamed data, `SKIP LOCKED` priority queues, delayed/scheduled retry via TTL tables. The key insight: Kafka-style streaming and Hangfire-style job queue patterns coexist in the same database.
4. **Background worker for TTL/scheduling** — PostgreSQL's Background Worker API (`bgworker.h`) allows extensions to run scheduled tasks inside PG. Combined with [PostgresAot](../PostgresAot/), this could enable a C# NativeAOT background worker that handles TTL expiration, delayed retries, and timer-based queue management — no external schedulers needed.
5. **Client overhead research** — measure how much latency/throughput is lost in the Npgsql client layer vs raw protocol. Explore a "Raw ORM" approach: mapping directly from the replication protocol stream to domain objects, bypassing intermediate structures (like `IDataReader` materializations). Need to investigate Npgsql's internal pipeline first.
