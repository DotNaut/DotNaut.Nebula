Before stars are born, there is a nebula — a vast, chaotic cloud of raw matter. This is ours. A collection of small, loosely connected experiments, prototypes, and wild ideas from the DotNaut universe.

## Labs

- [dotnet/aot/postgres-extension](dotnet/aot/postgres-extension/) — PostgreSQL extension in pure C# NativeAOT
- [postgres/as/kafka](postgres/as/kafka/) — WAL replication as event streaming alternative
- [keepassxc/client/dotnet](keepassxc/client/dotnet/) — lightweight .NET client for KeePassXC

## Organization

We are exploring a [mesh](infonomics/mesh/) concept to organize the chaos. Borrowing ideas from software design - single responsibility, abstractions, dependency direction - and applying them to knowledge. Each document is a node. Each `## Mesh` section declares its relationships, e.g.:
- `Part of` - belongs to a parent subject
- `Has` - contains child nodes
- `Implements` / `Implemented by` - abstraction vs. concrete
- `Uses` / `Used by` - dependency direction

## Subjects

- **[dotnet](dotnet/)** — Microsoft's open-source development platform
  - [aot/postgres-extension](dotnet/aot/postgres-extension/) — PostgreSQL extension in pure C# NativeAOT
- **[postgres](postgres/)** — the most versatile open-source database
  - [wal](postgres/wal/) — write-ahead log
  - [replication/logical](postgres/replication/logical.md) — logical replication
  - [as/kafka](postgres/as/kafka/) — WAL replication as event streaming alternative
- **[ssh](ssh/)** — secure shell
  - [keygen](ssh/keygen.md) — key pair generation
  - [agent](ssh/agent.md) — key management in memory
- **[windows](windows/)** — platform-specific setup
  - [ssh/agent](windows/ssh/agent.md) — OpenSSH Agent service
- **[keepassxc](keepassxc/)** — password manager
  - [cli](keepassxc/cli/) — command-line interface
  - [ssh-agent-provider](keepassxc/ssh-agent-provider/) — SSH key storage, injects into agent
  - [client/dotnet](keepassxc/client/dotnet/) — lightweight .NET client
  