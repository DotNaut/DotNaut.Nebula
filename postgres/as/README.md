# Postgres as...

Teams are using Postgres as a replacement for specialized tools: message queues, caches, event streaming, job schedulers. One fewer system to operate, and Postgres is already there.

Our experiments explore this boundary: where does Postgres genuinely replace a dedicated tool, and where does it just pretend to?

## Labs

- [kafka](kafka/) — WAL replication as event streaming alternative

## Mesh

- Part of [PostgreSQL](../README.md)
