# PostgreSQL WAL

Write-Ahead Log - append-only, ordered, durable log of every change. Exists primarily for crash recovery: every transaction is written to WAL before data files.

## Mesh

- Part of [PostgreSQL](../README.md)
- Used by [Logical Replication](../replication/logical.md)