Before stars are born, there is a nebula — a vast, chaotic cloud of raw matter. This is ours. A collection of small, loosely connected experiments, prototypes, and wild ideas from the DotNaut universe.

We are exploring a [mesh](infonomics/mesh/) concept to organize the chaos. Borrowing ideas from software design - single responsibility, abstractions, dependency direction - and applying them to knowledge. Each document is a node. Each `## Mesh` section declares its relationships, e.g.:
- `Part of` - belongs to a parent subject
- `Has` - contains child nodes
- `Implements` / `Implemented by` - abstraction vs. concrete
- `Uses` / `Used by` - dependency direction

## Subjects

- **[ssh](ssh/)** — secure shell
  - [keygen](ssh/keygen.md) — key pair generation
  - [agent](ssh/agent.md) — key management in memory
- **[windows](windows/)** — platform-specific setup
  - [ssh/agent](windows/ssh/agent.md) — OpenSSH Agent service
- **[PostgresAot](PostgresAot/)** — PostgreSQL extension written entirely in C# via NativeAOT. No C code, 6.5 KB with bflat.
- **[PgKafkaWal](PgKafkaWal/)** — PostgreSQL logical replication as a Kafka alternative.
- **[keepassxc](keepassxc/)** — password manager
  - [ssh-agent-provider](keepassxc/ssh-agent-provider.md) — SSH key storage, injects into agent
  - [client/dotnet](keepassxc/client/dotnet/) — lightweight .NET client