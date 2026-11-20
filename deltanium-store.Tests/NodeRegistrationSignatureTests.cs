using System;
using System.Security.Cryptography;
using System.Text;
using DeltaniumStore;
using FluentAssertions;
using NBitcoin;
using NBitcoin.Crypto;
using Xunit;

namespace DeltaniumStore.Tests;

[Collection("NetworkScope")]
public class NodeRegistrationSignatureTests : IDisposable
{
    public void Dispose() => NetworkScope.Id = "";

    [Fact]
    public void Registration_is_signed_over_the_message_Central_checks()
    {
        NetworkScope.Id = "deltanium-test";
        var key = new Key();
        var pk = key.PubKey.ToHex();
        var msg = NodeRegistrationService.RegistrationMessage("store", pk, "https://n.example/", " tcps://auto:7333", "V1", 42);
        msg.Should().Be($"NODE-REGISTER|store|{pk}|https://n.example|tcps://auto:7333|V1|42");

        var sig = NodeRegistrationService.SignRegistration(key, "store", pk, "https://n.example/", " tcps://auto:7333", "V1", 42);
        var hash = new uint256(SHA256.HashData(Encoding.UTF8.GetBytes("deltanium-test\n" + msg)));
        key.PubKey.Verify(hash, ECDSASignature.FromDER(Convert.FromBase64String(sig))).Should().BeTrue();
    }
}
