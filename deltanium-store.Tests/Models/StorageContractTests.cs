using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeltaniumStore.Models;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace deltanium_store.Tests.Models;

public class StorageContractTests
{
    [Fact]
    public void GetContractContent_ContainsRequiredFields_AndNoHashOrSignatures()
    {
        var c = TestDataBuilder.CreateStorageContract(
            appPublicKey: "appPK",
            storageNodePublicKey: "nodePK",
            startDateUnix: 1000,
            endDateUnix: 2000,
            contractType: "TimeFixed",
            totalFee: 0.0000001000m,
            totalFileSize: 2048,
            fileIds: new List<string> { "f1", "f2" });
        c.ContractId = "cid1";
        c.CreatedAtUnix = 500;
        c.Status = "Active";

        var content = StorageContract.GetContractContent(c);

        content.Should().ContainKey("appPublicKey").WhoseValue.Should().Be("appPK");
        content.Should().ContainKey("storageNodePublicKey").WhoseValue.Should().Be("nodePK");
        content.Should().ContainKey("contractId").WhoseValue.Should().Be("cid1");
        content.Should().ContainKey("contractType").WhoseValue.Should().Be("TimeFixed");
        content.Should().ContainKey("startDateUnix").WhoseValue.Should().Be(1000L);
        content.Should().ContainKey("endDateUnix").WhoseValue.Should().Be(2000L);
        content.Should().ContainKey("totalFee").WhoseValue.Should().Be("0.0000001000");
        content.Should().ContainKey("totalFileSize").WhoseValue.Should().Be(2048L);
        content.Should().ContainKey("status").WhoseValue.Should().Be("Active");
        content.Should().ContainKey("fileIds");
        content.Should().NotContainKey("contractHash");
        content.Should().NotContainKey("appSignature");
        content.Should().NotContainKey("storageNodeSignature");
    }

    [Fact]
    public void GetContractContent_OpenEnded_OmitsEndDateUnix()
    {
        var c = TestDataBuilder.CreateStorageContract(contractType: "OpenEnded", endDateUnix: null);

        var content = StorageContract.GetContractContent(c);

        content.Should().NotContainKey("endDateUnix");
    }

    [Fact]
    public void GetContractContent_TimeFixed_IncludesEndDateUnix()
    {
        var c = TestDataBuilder.CreateStorageContract(
            contractType: "TimeFixed",
            startDateUnix: 100,
            endDateUnix: 200);

        var content = StorageContract.GetContractContent(c);

        content.Should().ContainKey("endDateUnix").WhoseValue.Should().Be(200L);
    }

    [Fact]
    public void GetContractContent_FileIdsAreSorted()
    {
        var c = TestDataBuilder.CreateStorageContract(fileIds: new List<string> { "z", "a", "m" });

        var content = StorageContract.GetContractContent(c);
        var fileIds = (content["fileIds"] as IEnumerable<string>)!.ToList();

        fileIds.Should().ContainInOrder("a", "m", "z");
    }

    [Fact]
    public void ComputeContractHash_IsDeterministic()
    {
        var c = TestDataBuilder.CreateStorageContract(
            contractId: "same",
            appPublicKey: "app",
            storageNodePublicKey: "node",
            startDateUnix: 1,
            totalFee: 0.1m,
            totalFileSize: 100,
            fileIds: new List<string> { "f1" });
        c.CreatedAtUnix = 1;
        c.Status = "Active";

        var hash1 = StorageContract.ComputeContractHash(c);
        var hash2 = StorageContract.ComputeContractHash(c);

        hash1.Should().Be(hash2);
        hash1.Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    public void ComputeContractHash_DifferentContent_DifferentHash()
    {
        var c1 = TestDataBuilder.CreateStorageContract(contractId: "id1", totalFee: 0.1m);
        var c2 = TestDataBuilder.CreateStorageContract(contractId: "id2", totalFee: 0.1m);

        var hash1 = StorageContract.ComputeContractHash(c1);
        var hash2 = StorageContract.ComputeContractHash(c2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeContractHash_MatchesGetContractContent_SerializedThenHashed()
    {
        var c = TestDataBuilder.CreateStorageContract(
            contractId: "cid",
            appPublicKey: "appPK",
            storageNodePublicKey: "nodePK",
            startDateUnix: 12345,
            totalFee: 0.0000001000m,
            totalFileSize: 999);
        c.CreatedAtUnix = 1000;
        c.Status = "Active";

        var content = StorageContract.GetContractContent(c);
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = false });
        var expectedHash = StorageContract.ComputeContractHash(c);

        var hashFromContent = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        hashFromContent.Should().Be(expectedHash);
    }
}
