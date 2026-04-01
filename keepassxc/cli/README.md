# KeePassXC CLI

Command-line interface for KeePassXC. Ships with KeePassXC installation at `C:\Program Files\KeePassXC\keepassxc-cli.exe`.

## Key commands

```bash
keepassxc-cli add <db> <entry>                      # create entry
keepassxc-cli edit <db> <entry> -u <user>            # edit entry
keepassxc-cli show <db> <entry>                      # show entry details
keepassxc-cli attachment-import <db> <entry> <name> <file>  # import file as attachment
keepassxc-cli attachment-export <db> <entry> <name>  # export attachment
keepassxc-cli ls <db>                                # list entries
keepassxc-cli search <db> <term>                     # search
```

## Limitations

- Cannot configure SSH Agent settings (add on unlock, remove on lock) — GUI only
- Workaround: import `KeeAgent.settings` XML as attachment (see [SSH Agent Provider](../ssh-agent-provider/))

## Mesh

- Part of [KeePassXC](../README.md)
- Used by [SSH Agent Provider](../ssh-agent-provider/)
