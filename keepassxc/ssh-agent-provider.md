# KeePassXC SSH Agent Provider

KeePassXC can store SSH private keys in its encrypted database and inject them into SSH Agent automatically. Keys exist in memory only while the database is open.

## Enable (one-time)

1. Tools - Settings - SSH Agent
2. Check "Enable SSH Agent integration"
3. Select "OpenSSH" (not Pageant) on Windows
4. OK

## Create key entry

1. Create new entry
2. SSH Agent tab (in Advanced section)
3. Generate a new key ([SSH Keygen](../ssh/keygen.md), Ed25519) or import existing private key file
4. Check "Add key to agent when database is opened/unlocked"
5. Check "Remove key from agent when database is closed/locked"
6. **Uncheck** "Require user confirmation when this key is used" - Windows OpenSSH Agent does not support this, causes `Agent refused this identity` error
7. Save

## Verify

```powershell
ssh-add -l
# Should list the key:
# 256 SHA256:... key-comment (ED25519)
```

If the key doesn't appear:
- Lock and unlock the database
- Check that OpenSSH Agent service is running (see [SSH Agent on Windows](../windows/ssh/agent.md))
- Check that "Add to agent" is enabled in the entry

## After setup

Once the key is in KeePassXC, the private key file can be removed from disk. The key lives only in the encrypted database.

## Mesh

- Part of [KeePassXC](README.md)
- Uses [SSH Agent](../ssh/agent.md)
