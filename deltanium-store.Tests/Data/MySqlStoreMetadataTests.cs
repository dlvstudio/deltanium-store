using DeltaniumStore.Data;
using deltanium_store.Tests.Helpers;
using FluentAssertions;

namespace deltanium_store.Tests.Data;

public class MySqlStoreMetadataTests : IDisposable
{
    private readonly InMemoryStoreDbFactory _factory;
    private readonly MySqlFileMetadataStore _files;
    private readonly MySqlBlockMetadataStore _blocks;
    private readonly MySqlContractStore _contracts;

    public MySqlStoreMetadataTests()
    {
        _factory = new InMemoryStoreDbFactory();
        _files = new MySqlFileMetadataStore(_factory);
        _blocks = new MySqlBlockMetadataStore(_factory);
        _contracts = new MySqlContractStore(_factory);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void FileMetadata_Upsert_Load_Delete()
    {
        var meta = TestDataBuilder.CreateFileMetadata(fileId: "file-1");

        _files.Upsert(meta);
        _files.LoadAll().Should().ContainKey("file-1");

        meta.Type = "post";
        _files.Upsert(meta);
        _files.LoadAll()["file-1"].Type.Should().Be("post");

        _files.Delete("file-1");
        _files.LoadAll().Should().NotContainKey("file-1");
    }

    [Fact]
    public void BlockMetadata_Upsert_Load_Delete()
    {
        var meta = TestDataBuilder.CreateBlockMetadata(blockId: "block-1", blockIndex: 3);

        _blocks.Upsert(meta);
        var loaded = _blocks.LoadAll();
        loaded.Should().ContainKey("block-1");
        loaded["block-1"].BlockIndex.Should().Be(3);

        _blocks.Delete("block-1");
        _blocks.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void ContractStore_Upsert_AndUpdate()
    {
        var contract = TestDataBuilder.CreateStorageContract(contractId: "c1");
        _contracts.Upsert(contract);

        _contracts.LoadAll().Should().ContainKey("c1");

        contract.Status = "Closed";
        _contracts.Upsert(contract);

        _contracts.LoadAll()["c1"].Status.Should().Be("Closed");
    }
}
