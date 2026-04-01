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
3. Generate a key with [SSH Keygen](../../ssh/keygen.md) (Ed25519)
4. Import the private key file — use **Attachment**, not "External file" (external is only a reference — key is lost if file is deleted)
5. Check "Add key to agent when database is opened/unlocked"
5. Check "Remove key from agent when database is closed/locked"
6. **Uncheck** "Require user confirmation when this key is used" - Windows OpenSSH Agent does not support this, causes `Agent refused this identity` error
7. Save
8. Verify: `ssh-add -l` should list the key
9. Delete the private key file from disk — it now lives only in the encrypted database

If the key doesn't appear in `ssh-add -l`:
- Lock and unlock the database
- Check that OpenSSH Agent service is running (see [SSH Agent on Windows](../../windows/ssh/agent.md))
- Check that "Add to agent" is enabled in the entry

Or automate with [create-key.ps1](create-key.ps1) — generates key, imports into KeePassXC with agent settings, cleans up.

## Mesh

- Part of [KeePassXC](../README.md)
- Uses [SSH Agent](../../ssh/agent.md)
- Uses [CLI](../cli/) — keepassxc-cli for automation
