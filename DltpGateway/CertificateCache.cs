using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace Deltanium.DltpGateway;

/// <summary>
/// TLS certificates by DNS name: PEM files from Gateway:Certificates:&lt;host&gt;:Pem / :Key when configured
/// (Linux, Caddy, certbot), otherwise LocalMachine\WebHosting then \My (where IIS / win-acme keep them).
/// Cached for an hour so renewed certificates are picked up without a restart.
/// </summary>
public class CertificateCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (X509Certificate2? Cert, DateTime Until)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _config;
    private readonly ILogger<CertificateCache> _log;

    public CertificateCache(IConfiguration config, ILogger<CertificateCache> log)
    {
        _config = config;
        _log = log;
    }

    public X509Certificate2? Get(string host)
    {
        if (_cache.TryGetValue(host, out var hit) && DateTime.UtcNow < hit.Until) return hit.Cert;
        var cert = FromPem(host) ?? Find(host);
        _cache[host] = (cert, DateTime.UtcNow.Add(Ttl));
        return cert;
    }

    private X509Certificate2? FromPem(string host)
    {
        var pem = _config[$"Gateway:Certificates:{host}:Pem"];
        if (string.IsNullOrWhiteSpace(pem)) return null;
        var key = _config[$"Gateway:Certificates:{host}:Key"];
        try
        {
            using var loaded = string.IsNullOrWhiteSpace(key)
                ? X509Certificate2.CreateFromPemFile(pem)
                : X509Certificate2.CreateFromPemFile(pem, key);
            // Re-import so SslStream can use the private key on every platform.
            return new X509Certificate2(loaded.Export(X509ContentType.Pkcs12));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not load PEM certificate for {Host}: {Error}", host, ex.Message);
            return null;
        }
    }

    private static X509Certificate2? Find(string host)
    {
        foreach (var storeName in new[] { "WebHosting", "My" })
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch { continue; }
            var now = DateTime.Now;
            var match = store.Certificates
                .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now && Matches(c, host))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    private static bool Matches(X509Certificate2 c, string host)
    {
        var name = c.GetNameInfo(X509NameType.DnsName, false);
        if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ext in c.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            foreach (var dns in ext.EnumerateDnsNames())
                if (string.Equals(dns, host, StringComparison.OrdinalIgnoreCase) ||
                    (dns.StartsWith("*.") && host.EndsWith(dns[1..], StringComparison.OrdinalIgnoreCase) &&
                     host.Count(ch => ch == '.') == dns.Count(ch => ch == '.')))
                    return true;
        return false;
    }
}
