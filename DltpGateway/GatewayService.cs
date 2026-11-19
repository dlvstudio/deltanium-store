using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Deltanium.DltpGateway;

/// <summary>Listens on Gateway:Port, terminates TLS by SNI and forwards each DLTP request to the node over HTTPS.</summary>
public class GatewayService : BackgroundService
{
    // Hop-by-hop / transport headers that must not be copied between DLTP and HTTP.
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Content-Length"
    };

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly CertificateCache _certs;
    private readonly ILogger<GatewayService> _log;

    public GatewayService(IConfiguration config, IHttpClientFactory http, CertificateCache certs, ILogger<GatewayService> log)
    {
        _config = config;
        _http = http;
        _certs = certs;
        _log = log;
    }

    private Dictionary<string, string> Routes() =>
        _config.GetSection("Gateway:Routes").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key.Trim().ToLowerInvariant(), c => c.Value!.Trim().TrimEnd('/'));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var port = _config.GetValue("Gateway:Port", 7333);
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        _log.LogInformation("DLTP gateway listening on port {Port} for {Routes}", port, string.Join(", ", Routes().Keys));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => HandleAsync(client, stop), stop);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using var _ = client;
        client.NoDelay = true;
        await using var raw = client.GetStream();
        await using var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        var routes = Routes();
        string? sni = null;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(stop);
            hs.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificateSelectionCallback = (_, host) =>
                {
                    sni = (host ?? "").ToLowerInvariant();
                    return routes.ContainsKey(sni) ? _certs.Get(sni) : null;
                }
            }, hs.Token);
            if (sni == null || !routes.TryGetValue(sni, out var upstream)) return;

            await Wire.ExpectPrefaceAsync(ssl, hs.Token);
            await Wire.WritePrefaceAsync(ssl, stop);

            var http = _http.CreateClient("upstream");
            var port = _config.GetValue("Gateway:Port", 7333);
            while (!stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(TimeSpan.FromMinutes(5));
                var req = await Wire.ReadFrameOrEndAsync<DltpRequest>(ssl, idle.Token);
                if (req == null) break;
                var res = await ForwardAsync(http, upstream, $"{sni}:{port}", req, stop);
                await Wire.WriteFrameAsync(ssl, res, stop);
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException)
        {
            _log.LogDebug("DLTP connection {Remote} ({Sni}) ended: {Error}", remote, sni, ex.Message);
        }
    }

    private async Task<DltpResponse> ForwardAsync(HttpClient http, string upstream, string defaultAuthority, DltpRequest req, CancellationToken ct)
    {
        try
        {
            if (!req.Path.StartsWith('/')) return Error(req, 400, "Path must start with /");
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), upstream + req.Path);
            var body = string.IsNullOrEmpty(req.Body) ? null : Convert.FromBase64String(req.Body);
            if (body != null) msg.Content = new ByteArrayContent(body);
            foreach (var (k, v) in req.Headers)
            {
                if (SkipHeaders.Contains(k)) continue;
                if (!msg.Headers.TryAddWithoutValidation(k, v))
                    msg.Content?.Headers.TryAddWithoutValidation(k, v);
            }
            // The host:port the caller addressed; the node checks Central signatures against it.
            msg.Headers.Remove("X-Dltp-Authority");
            msg.Headers.TryAddWithoutValidation("X-Dltp-Authority",
                req.Headers.TryGetValue("Host", out var h) && !string.IsNullOrWhiteSpace(h) ? h : defaultAuthority);

            using var res = await http.SendAsync(msg, ct);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            var out_ = new DltpResponse { Id = req.Id, Status = (int)res.StatusCode, Body = bytes.Length > 0 ? Convert.ToBase64String(bytes) : null };
            foreach (var hdr in res.Headers.Concat(res.Content.Headers))
                if (!SkipHeaders.Contains(hdr.Key)) out_.Headers[hdr.Key] = string.Join(",", hdr.Value);
            return out_;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            _log.LogWarning("Forward {Method} {Path} to {Upstream} failed: {Error}", req.Method, req.Path, upstream, ex.Message);
            return Error(req, 502, "Upstream unavailable");
        }
    }

    private static DltpResponse Error(DltpRequest req, int status, string text) => new()
    {
        Id = req.Id,
        Status = status,
        Headers = { ["Content-Type"] = "text/plain" },
        Body = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
    };
}
