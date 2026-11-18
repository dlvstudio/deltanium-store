using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaniumStore;
using DeltaniumStore.Services;
using FluentAssertions;
using NBitcoin;
using Xunit;

namespace deltanium_store.Tests.Services;

public class VersionOriginTests
{
    [Fact]
    public void Store_version_is_signed_with_the_node_key_for_that_tx()
    {
        var key = new Key();
        var o = BlockchainService.VersionOrigin("tx1", key)!;
        o.Service.Should().Be("store");
        o.Version.Should().Be(BuildInfo.Version);
        o.PublicKey.Should().Be(key.PubKey.ToHex());
        bool Verifies(string txId) => key.PubKey.Verify(
            new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope($"DELTANIUM-TX-VERSION\n{txId}\nstore\n{o.Version}\n{o.Commit}")))),
            Convert.FromBase64String(o.Signature));
        Verifies("tx1").Should().BeTrue();
        Verifies("tx2").Should().BeFalse();
        JsonSerializer.Serialize(o).Should().Contain("\"publicKey\"").And.Contain("\"service\":\"store\"");
    }

    [Fact]
    public void No_node_key_means_no_origin() => BlockchainService.VersionOrigin("tx1", null).Should().BeNull();
}
