using FluentAssertions;
using DeltaniumStore;
using Xunit;

namespace DeltaniumStore.Tests.Controllers;

public class BuildInfoTests
{
    [Fact]
    public void Reports_csproj_version_and_build_time()
    {
        BuildInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$"); // set by the csproj or -p:Version from the release tag
        BuildInfo.BuiltAtUtc.Should().NotBeNullOrEmpty();
        BuildInfo.Describe("x").Should().NotBeNull();
    }
}
