using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NBitcoin;

namespace DeltaniumStore.Security
{
    /// <summary>
    /// Authenticates Central's calls to the internal /api/proof/* endpoints by the Central signature
    /// (headers X-Central-Timestamp, X-Central-Signature), replacing the shared X-Internal-Token that every
    /// Store operator had to know. Signed message (same as Central's CentralRequestSigner):
    /// "DELTANIUM-CENTRAL\n{METHOD}\n{host}\n{path}\n{unixSeconds}\n{sha256hex(body)}" over NetworkScope.
    /// The host is this Store's own, so a signature sent to one Store is useless on another; each signed
    /// message is accepted once.
    ///
    /// Central public key: env CENTRAL_PUBLIC_KEY (or CentralApi:PublicKey), else fetched once over HTTPS from
    /// {CentralApi:BaseUrl}/api/blockcreatornode/decision-key. The old shared token is no longer accepted.
    /// </summary>
    public static class CentralRequestAuth
    {
        public const string ItemKey = "deltanium.central-authenticated";
        public const string TimestampHeader = "X-Central-Timestamp";
        public const string SignatureHeader = "X-Central-Signature";
        public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, DateTime> Used = new();
        private static readonly SemaphoreSlim KeyGate = new(1, 1);
        private static string? _centralKey;
        private static DateTime _nextFetch = DateTime.MinValue;

        /// <summary>For tests: pin the Central public key without config or network.</summary>
        public static string? CentralKeyOverride { get; set; }

        public static string BuildMessage(string method, string host, string path, string timestamp, string bodyHashHex) =>
            $"DELTANIUM-CENTRAL\n{method.ToUpperInvariant()}\n{host.ToLowerInvariant()}\n{path}\n{timestamp}\n{bodyHashHex}";

        public static bool IsAuthenticated(HttpContext ctx) => ctx.Items.ContainsKey(ItemKey);

        /// <summary>Check the request and mark it authenticated; the body stays readable for the controller.</summary>
        public static async Task<(bool Ok, string Error)> AuthenticateAsync(HttpContext ctx, IConfiguration config, IHttpClientFactory? http)
        {
            var req = ctx.Request;
            if (req.Headers.TryGetValue(SignatureHeader, out var sigH) && req.Headers.TryGetValue(TimestampHeader, out var tsH))
            {
                var (ok, err) = await VerifySignatureAsync(ctx, sigH.ToString(), tsH.ToString(), config, http);
                if (ok) { ctx.Items[ItemKey] = true; return (true, ""); }
                return (false, err);
            }
            return (false, "Central signature required");
        }

        private static async Task<(bool Ok, string Error)> VerifySignatureAsync(HttpContext ctx, string signature, string timestamp,
            IConfiguration config, IHttpClientFactory? http)
        {
            if (!long.TryParse(timestamp, out var ts)) return (false, "Invalid Central timestamp");
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
            if (age.Duration() > MaxClockSkew) return (false, "Central timestamp expired");

            var req = ctx.Request;
            req.EnableBuffering();
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            req.Body.Position = 0;
            var bodyHash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
            var message = BuildMessage(req.Method, req.Host.Value ?? "", req.Path.Value ?? "", timestamp, bodyHash);

            var centralKey = await GetCentralKeyAsync(config, http);
            if (string.IsNullOrWhiteSpace(centralKey)) return (false, "Central public key unknown");
            if (!Verify(centralKey, NetworkScope.Scope(message), signature))
            {
                // Via the DLTP gateway the request arrives with this node's HTTPS host; Central signed the TCP
                // authority it dialled (host:port). Accept that only when it is this node's own TcpEndpoint,
                // so a signature meant for another node still fails.
                var dltp = req.Headers["X-Dltp-Authority"].ToString().Trim().ToLowerInvariant();
                var own = OwnTcpAuthorities(config).FirstOrDefault(a => string.Equals(a, dltp, StringComparison.OrdinalIgnoreCase));
                if (dltp.Length == 0 || own == null)
                    return (false, "Invalid Central signature");
                message = BuildMessage(req.Method, own, req.Path.Value ?? "", timestamp, bodyHash);
                if (!Verify(centralKey, NetworkScope.Scope(message), signature)) return (false, "Invalid Central signature");
            }

            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
            var now = DateTime.UtcNow;
            foreach (var kv in Used) if (kv.Value < now) Used.TryRemove(kv.Key, out _);
            if (!Used.TryAdd(id, now + MaxClockSkew + MaxClockSkew)) return (false, "Central request already used");
            return (true, "");
        }

        /// <summary>
        /// Every host:port this node is reachable at over DLTP: NodeRegistration:TcpEndpoint (gateway),
        /// Dltp:PublicEndpoint when it names an address, and the address Central confirmed for "tcps://auto:PORT".
        /// </summary>
        public static IEnumerable<string> OwnTcpAuthorities(IConfiguration config)
        {
            var list = new List<string>();
            if (OwnTcpAuthority(config) is { } a) list.Add(a);
            if (DeltaniumStore.Dltp.NodeDltpService.AuthorityOf(config["Dltp:PublicEndpoint"]) is { } b) list.Add(b);
            if (DeltaniumStore.Dltp.NodeDltpService.AnnouncedAuthority is { } c) list.Add(c);
            return list;
        }

        /// <summary>host:port of this node's DLTP endpoint (NodeRegistration:TcpEndpoint = tcps://host:port), if any.</summary>
        public static string? OwnTcpAuthority(IConfiguration config)
        {
            var v = (config["NodeRegistration:TcpEndpoint"] ?? "").Trim().TrimEnd('/');
            if (v.Length == 0) return null;
            var i = v.IndexOf("://", StringComparison.Ordinal);
            var authority = i >= 0 ? v[(i + 3)..] : v;
            return authority.Contains(':') ? authority.ToLowerInvariant() : null;
        }

        public static bool Verify(string publicKeyHex, string scopedMessage, string signatureBase64)
        {
            try
            {
                var pub = new PubKey(publicKeyHex);
                var hash = new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(scopedMessage)));
                return pub.Verify(hash, Convert.FromBase64String(signatureBase64));
            }
            catch { return false; }
        }

        private static async Task<string?> GetCentralKeyAsync(IConfiguration config, IHttpClientFactory? http)
        {
            if (!string.IsNullOrWhiteSpace(CentralKeyOverride)) return CentralKeyOverride;
            var pinned = Environment.GetEnvironmentVariable("CENTRAL_PUBLIC_KEY") ?? config["CentralApi:PublicKey"];
            if (!string.IsNullOrWhiteSpace(pinned)) return pinned.Trim();
            if (_centralKey != null) return _centralKey;
            if (http == null || DateTime.UtcNow < _nextFetch) return null;

            await KeyGate.WaitAsync();
            try
            {
                if (_centralKey != null) return _centralKey;
                _nextFetch = DateTime.UtcNow.AddMinutes(1);
                var baseUrl = (config["CentralApi:BaseUrl"] ?? "").TrimEnd('/');
                if (baseUrl.Length == 0) return null;
                using var client = http.CreateClient("central-key");
                client.Timeout = TimeSpan.FromSeconds(10);
                using var doc = JsonDocument.Parse(await client.GetStringAsync($"{baseUrl}/api/blockcreatornode/decision-key"));
                var key = doc.RootElement.TryGetProperty("publicKey", out var pk) ? pk.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key)) _centralKey = key.Trim();
                return _centralKey;
            }
            catch { return null; }
            finally { KeyGate.Release(); }
        }
    }
}
