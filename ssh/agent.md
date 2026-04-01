# SSH Agent

Daemon that holds private keys in memory. Applications ask the agent to sign challenges - the key never leaves the agent process.

## How it works

1. Client connects to `ssh user@host`
2. Server sends a challenge
3. Client asks the agent to sign it with the matching key
4. Agent signs and returns - private key never touches the client process

If the agent has the key, `ssh` just works. No `-i keyfile` needed.

## Commands

```bash
ssh-add -l          # list key fingerprints
ssh-add -L          # list full public keys (useful for exporting)
ssh-add keyfile     # add a key
```

## Mesh

- Part of [SSH](README.md)
- Implemented by [Windows SSH Agent](../windows/ssh/agent.md)
- Used by [KeePassXC SSH Agent Provider](../keepassxc/ssh-agent-provider/)
