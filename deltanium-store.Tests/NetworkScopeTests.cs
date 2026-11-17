using System;
using DeltaniumStore;
using FluentAssertions;
using Xunit;

namespace DeltaniumStore.Tests;

[Collection("NetworkScope")]
public class NetworkScopeTests : IDisposable
{
    public void Dispose() => NetworkScope.Id = "";

    [Fact]
    public void Mainnet_LeavesMessageUnchanged()
    {
        NetworkScope.Id = "mainnet";
        NetworkScope.Scope("GET/api/x123").Should().Be("GET/api/x123");
        NetworkScope.Name.Should().Be("mainnet");
    }

    [Fact]
    public void Testnet_PrefixesMessage()
    {
        NetworkScope.Id = "deltanium-test";
        NetworkScope.Scope("GET/api/x123").Should().Be("deltanium-test\nGET/api/x123");
    }

    [Fact]
    public void RejectsBadId()
    {
        var act = () => NetworkScope.Id = "bad id";
        act.Should().Throw<InvalidOperationException>();
    }
}

[CollectionDefinition("NetworkScope", DisableParallelization = true)]
public class NetworkScopeCollection { }
