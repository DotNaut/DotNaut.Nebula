# SSH Keygen

Key pair generation for SSH authentication.

## Generate

```bash
ssh-keygen -t ed25519 -C "comment"
```

Ed25519 recommended - short keys, fast, modern. Supported everywhere since OpenSSH 6.5 (2014).

Other types:

- `ed25519` - recommended, 256-bit, fastest
- `ecdsa` - 256/384/521-bit, good but less preferred
- `rsa` - legacy, use 4096-bit minimum if required by server

## Change comment on existing key

```bash
ssh-keygen -c -C "new-comment" -f ~/.ssh/id_ed25519
```

Useful when a key was generated with a wrong or default comment. Re-import into [KeePassXC](../keepassxc/ssh-agent.md) after changing.

## View public key fingerprint

```bash
ssh-keygen -l -f ~/.ssh/id_ed25519.pub
```

## Mesh

- Part of [SSH](README.md)
- Used by [Agent](agent.md) — generated keys are loaded into agent