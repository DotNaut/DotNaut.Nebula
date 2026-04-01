# SSH Agent on Windows

Windows ships with OpenSSH Agent as a service, disabled by default.

## Setup (PowerShell as Administrator)

```powershell
Set-Service ssh-agent -StartupType Automatic
Start-Service ssh-agent
```

## Verify

```powershell
ssh-add -l
```

- `The agent has no identities.` - working, no keys loaded
- `Error connecting to agent: No such file or directory` - service not running

## Mesh

- Part of [Windows](../README.md)
- Implements [SSH Agent](../../ssh/agent.md)
