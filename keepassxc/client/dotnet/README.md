# KeePassXC Client for .NET

A lightweight .NET client for [KeePassXC](https://keepassxc.org/) browser integration protocol. Communicates with KeePassXC via named pipes using NaCl encryption (Curve25519-XSalsa20-Poly1305).

## Usage

```csharp
#:project $(HOME)/Labs/DotNaut.Nebula/KeePassXC/KeePassXC.csproj

using KeePassXC;

using var kp = new KeePassXCClient();
await kp.ConnectAsync();

var logins = await kp.GetLoginsAsync("https://myservice.example.com");
var login = logins[0];

Console.WriteLine($"User: {login.Username}");
Console.WriteLine($"Pass: {login.Password}");

// Access custom string fields (see "String Fields" section below)
var secret = login.StringFields["KPH: MySecret"];
```

## Prerequisites

- KeePassXC must be running with **Browser Integration** enabled (Settings > Browser Integration > Enable)
- On first connection, KeePassXC will prompt to associate the client with the database

## Association

The client stores its association key in `.keepassxc.json`. On connect, it searches for this file starting from the current working directory and walking up through parent directories until found. On first association (when no file exists), it creates `.keepassxc.json` in the current directory. This file must be preserved for subsequent connections without re-prompting.

## Important Notes

### URL Matching

KeePassXC matches entries **by domain**, not by full URL path. This means:

- Querying `https://example.com/service/foo` will return **all** entries with domain `example.com`, regardless of path
- To achieve unique matching, use **unique subdomains**: `https://foo.services.example` vs `https://bar.services.example`
- URLs **must** include the `https://` scheme, otherwise KeePassXC won't find entries

### String Fields (Custom Properties)

KeePassXC entries support custom string fields via "Advanced > Additional Attributes". To make these accessible through the browser integration protocol:

- The field name **must** start with `KPH: ` prefix (including the space after colon)
- Fields without the `KPH: ` prefix are **not returned** by `GetLogins`