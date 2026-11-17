using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DeltaniumStore;
using DeltaniumStore.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using Xunit;

namespace DeltaniumStore.Tests
{
    /// <summary>Central's calls to /api/proof/* are authenticated by the Central signature, not a shared token.</summary>
    [Collection("CentralKey")]
    public class CentralRequestAuthTests : IDisposable
    {
        private readonly Key _central = new();
        private static readonly IConfiguration NoToken = Config(new());

        public CentralRequestAuthTests() => CentralRequestAuth.CentralKeyOverride = _central.PubKey.ToHex();
        public void Dispose() => CentralRequestAuth.CentralKeyOverride = null;

        private static IConfiguration Config(Dictionary<string, string?> v) => new ConfigurationBuilder().AddInMemoryCollection(v).Build();

        private DefaultHttpContext Request(string host, string path, string body, Key signer, string? signedHost = null, string? signedBody = null, long? ts = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Host = new HostString(host);
            ctx.Request.Path = path;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var t = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedBody ?? body))).ToLowerInvariant();
            var msg = NetworkScope.Scope(CentralRequestAuth.BuildMessage("POST", signedHost ?? host, path, t, hash));
            var sig = signer.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(msg))));
            ctx.Request.Headers[CentralRequestAuth.TimestampHeader] = t;
            ctx.Request.Headers[CentralRequestAuth.SignatureHeader] = Convert.ToBase64String(sig.ToDER());
            return ctx;
        }

        [Fact]
        public void Message_format_matches_Central_signer()
        {
            // Must stay identical to DeltaniumApi.Services.CentralRequestSigner.BuildMessage.
            CentralRequestAuth.BuildMessage("post", "Store-1.Example", "/api/proof/challenge", "1700000000", "ab")
                .Should().Be("DELTANIUM-CENTRAL\nPOST\nstore-1.example\n/api/proof/challenge\n1700000000\nab");
        }

        [Fact]
        public async Task Central_signed_request_is_accepted_once_and_body_stays_readable()
        {
            var body = "{\"blockId\":\"b1\",\"nonce\":\"" + Guid.NewGuid() + "\"}";
            var ctx = Request("store-a.example", "/api/proof/challenge", body, _central);
            (await CentralRequestAuth.AuthenticateAsync(ctx, NoToken, null)).Ok.Should().BeTrue();
            CentralRequestAuth.IsAuthenticated(ctx).Should().BeTrue();
            (await new StreamReader(ctx.Request.Body).ReadToEndAsync()).Should().Be(body);

            var replay = Request("store-a.example", "/api/proof/challenge", body, _central);
            replay.Request.Headers[CentralRequestAuth.TimestampHeader] = ctx.Request.Headers[CentralRequestAuth.TimestampHeader];
            replay.Request.Headers[CentralRequestAuth.SignatureHeader] = ctx.Request.Headers[CentralRequestAuth.SignatureHeader];
            (await CentralRequestAuth.AuthenticateAsync(replay, NoToken, null)).Ok.Should().BeFalse();
        }

        [Fact]
        public async Task Signature_for_another_store_or_body_or_signer_is_refused()
        {
            var body = "{\"fileId\":\"" + Guid.NewGuid() + "\"}";
            (await CentralRequestAuth.AuthenticateAsync(Request("store-b.example", "/api/proof/export-metadata", body, _central, signedHost: "store-a.example"), NoToken, null)).Ok.Should().BeFalse();
            (await CentralRequestAuth.AuthenticateAsync(Request("store-a.example", "/api/proof/export-metadata", body, _central, signedBody: "{}"), NoToken, null)).Ok.Should().BeFalse();
            (await CentralRequestAuth.AuthenticateAsync(Request("store-a.example", "/api/proof/export-metadata", body, new Key()), NoToken, null)).Ok.Should().BeFalse();
            (await CentralRequestAuth.AuthenticateAsync(Request("store-a.example", "/api/proof/export-metadata", body, _central, ts: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()), NoToken, null)).Ok.Should().BeFalse();
        }

        [Fact]
        public async Task Shared_token_is_refused_unless_the_legacy_switch_is_on()
        {
            DefaultHttpContext TokenRequest()
            {
                var ctx = new DefaultHttpContext();
                ctx.Request.Method = "POST";
                ctx.Request.Path = "/api/proof/egress/drain";
                ctx.Request.Headers["X-Internal-Token"] = "tok";
                return ctx;
            }
            (await CentralRequestAuth.AuthenticateAsync(TokenRequest(), Config(new() { ["InternalApi:Token"] = "tok" }), null)).Ok.Should().BeFalse();
            (await CentralRequestAuth.AuthenticateAsync(TokenRequest(),
                Config(new() { ["InternalApi:Token"] = "tok", ["InternalApi:AcceptLegacyToken"] = "true" }), null)).Ok.Should().BeTrue();
        }
    }
}
