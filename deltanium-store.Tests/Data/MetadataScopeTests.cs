using DeltaniumStore.Data;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace deltanium_store.Tests.Data;

public class MetadataScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scope_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static DbContextOptions<StoreDbContext> MySqlOptions() =>
        new DbContextOptionsBuilder<StoreDbContext>()
            .UseMySql("Server=localhost;Database=x;User=x;Password=x", new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

    [Fact]
    public void Each_prefix_maps_to_its_own_tables_and_legacy_keeps_the_old_names()
    {
        var opts = MySqlOptions();
        using var s1 = new PrefixedStoreDbContextFactory(opts, "store1_").CreateDbContext();
        using var s2 = new PrefixedStoreDbContextFactory(opts, "store2_").CreateDbContext();
        using var legacy = new StoreDbContext(opts);

        string Table<T>(DbContext db) => db.Model.FindEntityType(typeof(T))!.GetTableName()!;
        Table<FileMetadataRecord>(s1).Should().Be("store1_file_metadata");
        Table<BlockMetadataRecord>(s2).Should().Be("store2_block_metadata");
        Table<StorageContractRecord>(s2).Should().Be("store2_storage_contracts");
        Table<FileMetadataRecord>(legacy).Should().Be("file_metadata");
    }

    [Fact]
    public void Create_script_only_creates_tables_with_if_not_exists_semantics()
    {
        using var db = new PrefixedStoreDbContextFactory(MySqlOptions(), "store1_").CreateDbContext();
        var script = db.Database.GenerateCreateScript();
        script.Should().Contain("CREATE TABLE `store1_file_metadata`");
    }

    [Theory]
    [InlineData("store1_; DROP TABLE x")]
    [InlineData("a-b")]
    [InlineData("`x`")]
    public void Unsafe_prefixes_are_rejected(string prefix)
    {
        var act = () => StoreDbContext.ValidatePrefix(prefix);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Copy_moves_only_rows_whose_blocks_are_on_this_store()
    {
        var shared = Path.Combine(_root, "shared");
        var mine = Path.Combine(_root, "mine");
        var blockData = Path.Combine(_root, "block_data");
        Directory.CreateDirectory(blockData);

        var from = (f: new JsonFileMetadataStore(shared), b: new JsonBlockMetadataStore(shared), c: new JsonContractStore(shared));
        var to = (f: new JsonFileMetadataStore(mine), b: new JsonBlockMetadataStore(mine), c: new JsonContractStore(mine));

        var localFile = TestDataBuilder.CreateFileMetadata(fileId: "local-file", firstBlockId: "blk-local");
        var otherFile = TestDataBuilder.CreateFileMetadata(fileId: "other-file", firstBlockId: "blk-other");
        from.f.Upsert(localFile);
        from.f.Upsert(otherFile);
        from.b.Upsert(TestDataBuilder.CreateBlockMetadata(blockId: "blk-local"));
        from.b.Upsert(TestDataBuilder.CreateBlockMetadata(blockId: "blk-local-2", blockIndex: 1));
        from.b.Upsert(TestDataBuilder.CreateBlockMetadata(blockId: "blk-other"));
        from.c.Upsert(TestDataBuilder.CreateStorageContract(contractId: "c-local", fileIds: new() { "local-file" }));
        from.c.Upsert(TestDataBuilder.CreateStorageContract(contractId: "c-other", fileIds: new() { "other-file" }));
        File.WriteAllText(Path.Combine(blockData, "blk-local.data"), "x");
        File.WriteAllText(Path.Combine(blockData, "blk-local-2.data"), "x");

        var r = MetadataScopeMigration.CopyLocalRows(from.f, from.b, from.c, to.f, to.b, to.c, blockData);

        r.Should().Be(new MetadataScopeMigration.Result(1, 2, 1));
        to.f.LoadAll().Keys.Should().BeEquivalentTo("local-file");
        to.b.LoadAll().Keys.Should().BeEquivalentTo("blk-local", "blk-local-2");
        to.c.LoadAll().Keys.Should().BeEquivalentTo("c-local");
        from.f.LoadAll().Should().HaveCount(2, "the shared tables are left as they were");
    }
}
