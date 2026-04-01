# PostgreSQL Logical Replication

Turns WAL's internal change stream into an external one. Decodes raw WAL records into logical changes (INSERT, UPDATE, DELETE) and delivers them to subscribers.

Key primitives:

- **Publication** - what to stream (which tables)
- **Replication Slot** - read position bookmark (like Kafka consumer offset)
- **pgoutput** - protocol plugin that decodes WAL into structured messages

## Mesh

- Part of [PostgreSQL](../README.md)
- Uses [WAL](../wal/README.md) - reads the append-only log
- Used by [Postgres as Kafka](../as/kafka/) — event streaming over replication