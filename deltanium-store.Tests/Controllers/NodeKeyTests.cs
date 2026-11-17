using System;
using System.IO;
using DeltaniumStore.Security;
using FluentAssertions;
using NBitcoin;
using Xunit;

namespace DeltaniumStore.Tests.Controllers;

[Collection("NodeKeyEnv")]
public class NodeKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"node_info_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NODE_PRIVATE_KEY", null);
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void WriteNodeInfo(string publicKey, string? privateKey) =>
        File.WriteAllText(_path, privateKey == null
            ? $"{{\"publicKey\":\"{publicKey}\"}}"
            : $"{{\"publicKey\":\"{publicKey}\",\"privateKey\":\"{privateKey}\"}}");

    [Fact]
    public void Env_key_wins_and_node_info_needs_no_private_key()
    {
        var key = new Key();
        WriteNodeInfo(key.PubKey.ToHex(), null);
        Environment.SetEnvironmentVariable("NODE_PRIVATE_KEY", key.ToHex());
        NodeKey.Load(_path)!.PubKey.Should().Be(key.PubKey);
    }

    [Fact]
    public void Falls_back_to_node_info_key()
    {
        var key = new Key();
        WriteNodeInfo(key.PubKey.ToHex(), key.ToHex());
        NodeKey.Load(_path)!.PubKey.Should().Be(key.PubKey);
    }

    [Fact]
    public void Key_of_another_node_is_refused_without_revealing_it()
    {
        var key = new Key();
        var other = new Key();
        WriteNodeInfo(key.PubKey.ToHex(), null);
        Environment.SetEnvironmentVariable("NODE_PRIVATE_KEY", other.ToHex());
        FluentActions.Invoking(() => NodeKey.Load(_path)).Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(other.ToHex());
    }

    [Fact]
    public void No_key_anywhere_is_null()
    {
        WriteNodeInfo(new Key().PubKey.ToHex(), null);
        NodeKey.Load(_path).Should().BeNull();
    }
}
