using System.IO.Pipes;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NaCl;

namespace DotNaut.KeePassXC;

public class KeePassXCException(string message) : Exception(message);

public record LoginEntry(string Username, string Password, string Name, Dictionary<string, string> StringFields);

public class KeePassXCClient : IDisposable
{
    const string AssocFileName = ".keepassxc.json";

    readonly NamedPipeClientStream _pipe;
    readonly byte[] _publicKey;
    readonly byte[] _privateKey;
    readonly string _clientId;

    Curve25519XSalsa20Poly1305? _box;
    byte[]? _serverPublicKey;
    BigInteger _nonce;

    string? _assocId;
    byte[]? _assocKeyPublic;
    byte[]? _assocKeyPrivate;
    string? _assocFilePath;

    public KeePassXCClient()
    {
        var pipeName = $"org.keepassxc.KeePassXC.BrowserServer_{Environment.UserName}";
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        Curve25519XSalsa20Poly1305.KeyPair(out _privateKey, out _publicKey);
        _clientId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        _nonce = new BigInteger(RandomNumberGenerator.GetBytes(24).Concat(new byte[] { 0 }).ToArray());

        LoadAssociation();
    }

    public async Task ConnectAsync()
    {
        try
        {
            await _pipe.ConnectAsync(3000);
        }
        catch (TimeoutException)
        {
            throw new KeePassXCException("Cannot connect to KeePassXC. Is it running with Browser Integration enabled?");
        }

        await ExchangeKeysAsync();
        _box = new Curve25519XSalsa20Poly1305(_privateKey, _serverPublicKey!);

        if (_assocId != null && _assocKeyPublic != null)
        {
            if (await TestAssociateAsync())
                return;
        }

        await AssociateAsync();
    }

    public async Task<List<LoginEntry>> GetLoginsAsync(string url)
    {
        var msg = new JsonObject
        {
            ["action"] = "get-logins",
            ["url"] = url,
            ["keys"] = new JsonArray(new JsonObject
            {
                ["id"] = _assocId,
                ["key"] = Convert.ToBase64String(_assocKeyPublic!)
            })
        };

        var response = await SendEncryptedAsync(msg);
        var entries = response?["entries"]?.AsArray();
        var result = new List<LoginEntry>();

        if (entries != null)
        {
            foreach (var e in entries)
            {
                var fields = new Dictionary<string, string>();
                if (e!["stringFields"]?.AsArray() is { } sf)
                {
                    foreach (var field in sf)
                    {
                        var obj = field?.AsObject();
                        if (obj != null)
                        {
                            foreach (var kv in obj)
                                fields[kv.Key] = kv.Value?.GetValue<string>() ?? "";
                        }
                    }
                }

                result.Add(new LoginEntry(
                    e["login"]?.GetValue<string>() ?? "",
                    e["password"]?.GetValue<string>() ?? "",
                    e["name"]?.GetValue<string>() ?? "",
                    fields
                ));
            }
        }
        return result;
    }

    public async Task<bool> SetLoginAsync(string url, string login, string password, string? group = null)
    {
        var msg = new JsonObject
        {
            ["action"] = "set-login",
            ["url"] = url,
            ["submitUrl"] = url,
            ["id"] = _assocId,
            ["login"] = login,
            ["password"] = password
        };

        if (group != null)
            msg["group"] = group;

        var response = await SendEncryptedAsync(msg);
        return response?["success"]?.GetValue<string>() == "true";
    }

    // --- Protocol ---

    async Task ExchangeKeysAsync()
    {
        var msg = new JsonObject
        {
            ["action"] = "change-public-keys",
            ["publicKey"] = Convert.ToBase64String(_publicKey),
            ["nonce"] = Convert.ToBase64String(NextNonce()),
            ["clientID"] = _clientId
        };

        var resp = await SendRawAsync(msg);
        if (resp?["success"]?.GetValue<string>() != "true")
            throw new KeePassXCException("Key exchange failed");

        _serverPublicKey = Convert.FromBase64String(resp["publicKey"]!.GetValue<string>());
    }

    async Task AssociateAsync()
    {
        Curve25519XSalsa20Poly1305.KeyPair(out _assocKeyPrivate, out _assocKeyPublic);

        var msg = new JsonObject
        {
            ["action"] = "associate",
            ["key"] = Convert.ToBase64String(_publicKey),
            ["idKey"] = Convert.ToBase64String(_assocKeyPublic)
        };

        var resp = await SendEncryptedAsync(msg);
        _assocId = resp?["id"]?.GetValue<string>()
            ?? throw new KeePassXCException("Association failed — did you approve in KeePassXC?");

        SaveAssociation();
    }

    async Task<bool> TestAssociateAsync()
    {
        var msg = new JsonObject
        {
            ["action"] = "test-associate",
            ["id"] = _assocId,
            ["key"] = Convert.ToBase64String(_assocKeyPublic!)
        };

        try
        {
            var resp = await SendEncryptedAsync(msg);
            return resp?["success"]?.GetValue<string>() == "true";
        }
        catch { return false; }
    }

    // --- Crypto + Transport ---

    async Task<JsonObject?> SendEncryptedAsync(JsonObject innerMessage)
    {
        var nonce = NextNonce();
        var plaintext = Encoding.UTF8.GetBytes(innerMessage.ToJsonString());
        var ciphertext = new byte[plaintext.Length + Curve25519XSalsa20Poly1305.TagLength];

        _box!.Encrypt(ciphertext, plaintext, nonce);

        var wrapper = new JsonObject
        {
            ["action"] = innerMessage["action"]!.GetValue<string>(),
            ["message"] = Convert.ToBase64String(ciphertext),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["clientID"] = _clientId
        };

        var resp = await SendRawAsync(wrapper);

        if (resp?["message"] == null || resp?["nonce"] == null)
        {
            var error = resp?["error"]?.GetValue<string>() ?? resp?.ToJsonString() ?? "empty response";
            throw new KeePassXCException($"KeePassXC: {error}");
        }

        var respNonce = Convert.FromBase64String(resp["nonce"]!.GetValue<string>());
        var respCiphertext = Convert.FromBase64String(resp["message"]!.GetValue<string>());
        var respPlain = new byte[respCiphertext.Length - Curve25519XSalsa20Poly1305.TagLength];

        if (!_box.TryDecrypt(respPlain, respCiphertext, respNonce))
            throw new KeePassXCException("Failed to decrypt response from KeePassXC");

        return JsonNode.Parse(respPlain)?.AsObject();
    }

    async Task<JsonObject?> SendRawAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        await _pipe.WriteAsync(bytes);
        await _pipe.FlushAsync();

        var buf = new byte[1024 * 64];
        var read = await _pipe.ReadAsync(buf);

        return JsonNode.Parse(buf.AsSpan(0, read))?.AsObject();
    }

    byte[] NextNonce()
    {
        var bytes = _nonce.ToByteArray();
        var nonce = new byte[24];
        Array.Copy(bytes, nonce, Math.Min(bytes.Length, 24));
        _nonce++;
        return nonce;
    }

    // --- Persistence ---

    void SaveAssociation()
    {
        var data = new JsonObject
        {
            ["id"] = _assocId,
            ["pubKey"] = Convert.ToBase64String(_assocKeyPublic!),
            ["privKey"] = Convert.ToBase64String(_assocKeyPrivate!)
        };
        _assocFilePath ??= Path.Combine(Directory.GetCurrentDirectory(), AssocFileName);
        File.WriteAllText(_assocFilePath, data.ToJsonString());
    }

    void LoadAssociation()
    {
        _assocFilePath = FindAssocFile();
        if (_assocFilePath == null) return;
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(_assocFilePath))?.AsObject();
            _assocId = data?["id"]?.GetValue<string>();
            _assocKeyPublic = Convert.FromBase64String(data!["pubKey"]!.GetValue<string>());
            _assocKeyPrivate = Convert.FromBase64String(data!["privKey"]!.GetValue<string>());
        }
        catch { }
    }

    static string? FindAssocFile()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, AssocFileName);
            if (File.Exists(path))
                return path;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose()
    {
        _pipe.Dispose();
    }
}
