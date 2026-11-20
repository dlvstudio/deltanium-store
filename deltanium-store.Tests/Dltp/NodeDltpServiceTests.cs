using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumStore.Dltp;
using DeltaniumStore.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace DeltaniumStore.Tests.Dltp
{
    public class NodeDltpServiceTests
    {
        private sealed class NoServer : IServer
        {
            public IFeatureCollection Features { get; } = new FeatureCollection();
            public void Dispose() { }
            public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        [Fact]
        public void Authority_of_an_endpoint()
        {
            NodeDltpService.AuthorityOf("tcps://203.0.113.7:7333").Should().Be("203.0.113.7:7333");
            NodeDltpService.AuthorityOf("tcps://auto:7333").Should().BeNull();
            NodeDltpService.AuthorityOf("").Should().BeNull();
        }

        [Fact]
        public async Task Hello_proves_the_node_key_and_requests_reach_the_node()
        {
            // A tiny HTTP upstream standing in for the node's Kestrel: echoes method, path and X-Dltp-Authority.
            var upstream = new TcpListener(IPAddress.Loopback, 0);
            upstream.Start();
            var upPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var upTask = Task.Run(async () =>
            {
                using var c = await upstream.AcceptTcpClientAsync();
                var s = c.GetStream();
                var buf = new byte[8192];
                var head = "";
                while (!head.Contains("\r\n\r\n")) { var n = await s.ReadAsync(buf); if (n == 0) break; head += Encoding.ASCII.GetString(buf, 0, n); }
                var lines = head.Split("\r\n");
                var auth = lines.FirstOrDefault(l => l.StartsWith("X-Dltp-Authority:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                var body = Encoding.UTF8.GetBytes($"{lines[0]} | {auth}");
                var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(resp);
                await s.WriteAsync(body);
            });

            var port = FreePort();
            var key = new Key();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dltp:Port"] = port.ToString(),
                ["Dltp:Upstream"] = $"http://127.0.0.1:{upPort}",
            }).Build();
            var svc = new NodeDltpService(config, new NoServer(),
                new NodeDltpOptions { NodeKey = () => key, CentralUrl = () => null, Role = "storenode" },
                NullLogger<NodeDltpService>.Instance);
            await svc.StartAsync(default);
            try
            {
                using var tcp = new TcpClient();
                for (var i = 0; ; i++)
                {
                    try { await tcp.ConnectAsync(IPAddress.Loopback, port); break; }
                    catch (SocketException) when (i < 50) { await Task.Delay(100); }
                }
                await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync("203.0.113.7");
                await Wire.WritePrefaceAsync(ssl, default);
                await Wire.ExpectPrefaceAsync(ssl, default);

                await Wire.WriteFrameAsync(ssl, new DltpRequest { Id = 0, Method = "HELLO", Path = "/" }, default);
                var hello = (await Wire.ReadFrameOrEndAsync<DltpResponse>(ssl, default))!;
                hello.Headers["X-Node-PubKey"].Should().Be(key.PubKey.ToHex());
                var certHash = Convert.ToHexString(SHA256.HashData(ssl.RemoteCertificate!.GetRawCertData())).ToLowerInvariant();
                CentralRequestAuth.Verify(key.PubKey.ToHex(), NetworkScope.Scope("DLTP-ID|" + certHash), hello.Headers["X-Node-Proof"])
                    .Should().BeTrue("the proof signs the certificate this connection uses");

                await Wire.WriteFrameAsync(ssl, new DltpRequest
                {
                    Id = 7, Method = "GET", Path = "/api/version?x=1",
                    Headers = { ["Host"] = $"203.0.113.7:{port}" }
                }, default);
                var res = (await Wire.ReadFrameOrEndAsync<DltpResponse>(ssl, default))!;
                res.Id.Should().Be(7);
                res.Status.Should().Be(200);
                Encoding.UTF8.GetString(Convert.FromBase64String(res.Body!))
                    .Should().Be($"GET /api/version?x=1 HTTP/1.1 | 203.0.113.7:{port}");
                await upTask;
            }
            finally
            {
                await svc.StopAsync(default);
                upstream.Stop();
            }
        }
    }
}
