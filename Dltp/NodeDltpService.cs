using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NBitcoin;
using DeltaniumStore.Security;

namespace DeltaniumStore.Dltp;

/// <summary>What the built-in DLTP listener needs from the node.</summary>
public sealed class NodeDltpOptions
{
    /// <summary>The node's registered signing key (null until it has one).</summary>
    public required Func<Key?> NodeKey { get; init; }
    /// <summary>Central API base URL, e.g. https://api.deltanium.com.</summary>
    public required Func<string?> CentralUrl { get; init; }
    /// <summary>"storenode" or "blockcreatornode": the Central route for self/endpoints.</summary>
    public required string Role { get; init; }
}

/// <summary>
/// Built-in DLTP listener, for nodes on a home or personal computer: no IIS, reverse proxy, domain or
/// CA certificate needed. Turn it on with Dltp:Port (e.g. 7333) and let the node announce itself with
/// Dltp:PublicEndpoint = tcps://auto:7333 (Central fills in the public IP it sees) or tcps://&lt;public IP&gt;:7333.
///
/// TLS uses a self-signed certificate made at start-up. Clients that dial an IP address send a HELLO frame
/// first and the node answers with its public key and a signature over this certificate's SHA-256
/// (Scope("DLTP-ID|" + hash)), so they know they reached the registered node. Every other frame is handed to
/// the node's own HTTP pipeline (Dltp:Upstream, by default the address Kestrel listens on), exactly like HTTPS.
/// </summary>
public class NodeDltpService : BackgroundService
{
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Content-Length"
    };
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _config;
    private readonly IServer _server;
    private readonly NodeDltpOptions _node;
    private readonly ILogger<NodeDltpService> _log;
    private X509Certificate2 _cert = null!;   // made in ExecuteAsync, only when the listener is on
    private string _certHash = "";
    private readonly HttpClient _http;
    private string? _upstream;

    /// <summary>host:port of the DLTP endpoint Central last confirmed for this node (fills in "auto").</summary>
    public static string? AnnouncedAuthority { get; private set; }

    /// <summary>host:port of an endpoint like tcps://host:port, or null (also for the "auto" placeholder).</summary>
    public static string? AuthorityOf(string? endpoint)
    {
        var v = (endpoint ?? "").Trim().TrimEnd('/');
        var i = v.IndexOf("://", StringComparison.Ordinal);
        var a = (i >= 0 ? v[(i + 3)..] : v).ToLowerInvariant();
        return a.Contains(':') && !a.StartsWith("auto:") ? a : null;
    }

    public NodeDltpService(IConfiguration config, IServer server, NodeDltpOptions node, ILogger<NodeDltpService> log)
    {
        _config = config;
        _server = server;
        _node = node;
        _log = log;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Upstream is this node itself; on loopback accept its local development certificate.
            ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                errors == SslPolicyErrors.None || (req.RequestUri != null && req.RequestUri.IsLoopback)
        }) { Timeout = TimeSpan.FromSeconds(100) };
    }

    public static int Port(IConfiguration config) => config.GetValue("Dltp:Port", 0);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var port = Port(_config);
        if (port <= 0) return;
        // Never let the listener take the node down (BackgroundService failures stop the host in .NET 8).
        try
        {
            _cert = CreateCertificate();
            _certHash = Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _log.LogError("DLTP listener off: could not create its TLS certificate: {Error}", ex.Message);
            return;
        }
        // Never let the listener take the node down: if the port is busy (e.g. IIS overlapping a recycle), retry.
        TcpListener listener;
        while (true)
        {
            try
            {
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                listener.Start();
                break;
            }
            catch (SocketException ex)
            {
                _log.LogWarning("DLTP port {Port} unavailable ({Error}); retrying in 30 s", port, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
            }
        }
        _log.LogInformation("DLTP listening on port {Port} (certificate sha256 {Hash})", port, _certHash[..16]);
        _ = Task.Run(() => AnnounceLoopAsync(stop), stop);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => HandleAsync(client, port, stop), stop);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError("DLTP listener stopped: {Error}", ex.Message); }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, int port, CancellationToken stop)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using var _ = client;
        client.NoDelay = true;
        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                hs.CancelAfter(TimeSpan.FromSeconds(15));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, hs.Token);
                await Wire.ExpectPrefaceAsync(ssl, hs.Token);
                await Wire.WritePrefaceAsync(ssl, stop);
            }
            while (!stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(TimeSpan.FromMinutes(5));
                var req = await Wire.ReadFrameOrEndAsync<DltpRequest>(ssl, idle.Token);
                if (req == null) break;
                var res = string.Equals(req.Method, "HELLO", StringComparison.OrdinalIgnoreCase)
                    ? Hello(req)
                    : await ForwardAsync(req, port, stop);
                await Wire.WriteFrameAsync(ssl, res, stop);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("DLTP connection {Remote} ended: {Error}", remote, ex.Message);
        }
    }

    /// <summary>Answer HELLO: our public key and a signature over this TLS certificate.</summary>
    private DltpResponse Hello(DltpRequest req)
    {
        var key = _node.NodeKey();
        if (key == null) return Error(req, 503, "Node key not configured");
        var message = NetworkScope.Scope("DLTP-ID|" + _certHash);
        var proof = key.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(message)))).ToDER();
        return new DltpResponse
        {
            Id = req.Id,
            Status = 200,
            Headers = { ["X-Node-PubKey"] = key.PubKey.ToHex(), ["X-Node-Proof"] = Convert.ToBase64String(proof) }
        };
    }

    private async Task<DltpResponse> ForwardAsync(DltpRequest req, int port, CancellationToken ct)
    {
        var upstream = Upstream();
        if (upstream == null) return Error(req, 503, "Node HTTP address unknown (set Dltp:Upstream)");
        try
        {
            if (!req.Path.StartsWith('/')) return Error(req, 400, "Path must start with /");
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), upstream + req.Path);
            if (!string.IsNullOrEmpty(req.Body)) msg.Content = new ByteArrayContent(Convert.FromBase64String(req.Body));
            foreach (var (k, v) in req.Headers)
            {
                if (SkipHeaders.Contains(k)) continue;
                if (!msg.Headers.TryAddWithoutValidation(k, v)) msg.Content?.Headers.TryAddWithoutValidation(k, v);
            }
            // The host:port the caller dialled; Central signatures to this node bind to it.
            msg.Headers.Remove("X-Dltp-Authority");
            msg.Headers.TryAddWithoutValidation("X-Dltp-Authority",
                req.Headers.TryGetValue("Host", out var h) && !string.IsNullOrWhiteSpace(h) ? h : $"localhost:{port}");

            using var res = await _http.SendAsync(msg, ct);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            var out_ = new DltpResponse { Id = req.Id, Status = (int)res.StatusCode, Body = bytes.Length > 0 ? Convert.ToBase64String(bytes) : null };
            foreach (var hdr in res.Headers.Concat(res.Content.Headers))
                if (!SkipHeaders.Contains(hdr.Key)) out_.Headers[hdr.Key] = string.Join(",", hdr.Value);
            return out_;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            _log.LogWarning("DLTP forward {Method} {Path} failed: {Error}", req.Method, req.Path, ex.Message);
            return Error(req, 502, "Node unavailable");
        }
    }

    /// <summary>Dltp:Upstream, else the first address this node's Kestrel listens on (wildcards become loopback).</summary>
    private string? Upstream()
    {
        if (_upstream != null) return _upstream;
        var configured = _config["Dltp:Upstream"];
        if (!string.IsNullOrWhiteSpace(configured)) return _upstream = configured.Trim().TrimEnd('/');
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();
        var pick = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses.FirstOrDefault();
        if (pick == null) return null;
        var u = pick.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1")
                    .Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1");
        return _upstream = u.TrimEnd('/');
    }

    /// <summary>Tell Central our DLTP endpoint now and every 30 minutes (a home IP may change).</summary>
    private async Task AnnounceLoopAsync(CancellationToken stop)
    {
        var endpoint = _config["Dltp:PublicEndpoint"]?.Trim();
        if (string.IsNullOrEmpty(endpoint)) return;
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
        while (!stop.IsCancellationRequested)
        {
            try { await AnnounceAsync(endpoint, stop); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("DLTP endpoint announce failed: {Error}", ex.Message);
            }
            await Task.Delay(AnnounceEvery, stop);
        }
    }

    private async Task AnnounceAsync(string endpoint, CancellationToken ct)
    {
        var key = _node.NodeKey();
        var central = _node.CentralUrl()?.Trim().TrimEnd('/');
        if (key == null || string.IsNullOrEmpty(central)) return;
        if (central.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) central = central[..^4];
        var path = $"/api/{_node.Role}/self/endpoints";
        var body = System.Text.Json.JsonSerializer.Serialize(new { tcpEndpoint = endpoint });
        using var msg = new HttpRequestMessage(HttpMethod.Post, central + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var signed = NetworkScope.Scope("POST" + path + ts + bodyHash);
        var sig = key.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(signed))));
        msg.Headers.TryAddWithoutValidation("X-User-PubKey", key.PubKey.ToHex());
        msg.Headers.TryAddWithoutValidation("X-Timestamp", ts);
        msg.Headers.TryAddWithoutValidation("X-Signature", Convert.ToBase64String(sig.ToDER()));
        using var res = await _http.SendAsync(msg, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("tcpEndpoint", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                    AnnouncedAuthority = AuthorityOf(t.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            _log.LogInformation("DLTP endpoint announced to Central: {Endpoint}", AnnouncedAuthority);
        }
        else _log.LogWarning("Central refused DLTP endpoint {Endpoint}: {Status} {Body}", endpoint, (int)res.StatusCode, text);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=deltanium-node", ec, HashAlgorithmName.SHA256);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        // Re-import so SslStream (Schannel on Windows) can use the private key. Under IIS the app pool may have
        // no user profile to keep the key in; then use the machine key store.
        var pfx = made.Export(X509ContentType.Pkcs12);
        foreach (var flags in new[] { X509KeyStorageFlags.UserKeySet, X509KeyStorageFlags.MachineKeySet, X509KeyStorageFlags.EphemeralKeySet })
        {
            try { return new X509Certificate2(pfx, (string?)null, flags); }
            catch (CryptographicException) { }
        }
        throw new CryptographicException("no key store available for the TLS key (under IIS set the app pool's Load User Profile = true)");
    }

    private static DltpResponse Error(DltpRequest req, int status, string text) => new()
    {
        Id = req.Id,
        Status = status,
        Headers = { ["Content-Type"] = "text/plain" },
        Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
    };
}
