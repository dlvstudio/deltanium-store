using System.Text.Json;
using DeltaniumStore.Data;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace deltanium_store.Tests.Integration;

public class RealMysqlStoreIntegrationTests : IDisposable
{
    private readonly IDbContextFactory<StoreDbContext>? _factory;
    private readonly MySqlFileMetadataStore? _files;
    private readonly MySqlBlockMetadataStore? _blocks;
    private readonly MySqlContractStore? _contracts;
    private readonly string _tag;
    private readonly List<string> _ids = new();

    public RealMysqlStoreIntegrationTests()
    {
        _tag = $"it_{Guid.NewGuid():N}"[..12];
        var cs = TryLoadCs();
        if (cs == null) return;

        var options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseMySql(cs, new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;
        _factory = new SimpleFactory(options);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _files = new MySqlFileMetadataStore(_factory);
        _blocks = new MySqlBlockMetadataStore(_factory);
        _contracts = new MySqlContractStore(_factory);
    }

    private static string? TryLoadCs()
    {
        var env = Environment.GetEnvironmentVariable("DELTANIUM_MYSQL_CS");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".microsoft", "usersecrets", "deltanium-store-local-secrets", "secrets.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("ConnectionStrings:Default", out var v) ? v.GetString() : null;
    }

    public void Dispose()
    {
        if (_factory == null) return;
        try
        {
            using var db = _factory.CreateDbContext();
            foreach (var id in _ids)
            {
                var f = db.FileMetadataRecords.FirstOrDefault(x => x.Id == id);
                if (f != null) db.FileMetadataRecords.Remove(f);
                var b = db.BlockMetadataRecords.FirstOrDefault(x => x.Id == id);
                if (b != null) db.BlockMetadataRecords.Remove(b);
                var c = db.StorageContractRecords.FirstOrDefault(x => x.Id == id);
                if (c != null) db.StorageContractRecords.Remove(c);
            }
            db.SaveChanges();
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void RealDb_FileBlockContract_UpsertLoadDelete()
    {
        Assert.True(_files != null && _blocks != null && _contracts != null, "Missing store MySQL secrets");

        var fileId = $"{_tag}_file";
        var blockId = $"{_tag}_block";
        var contractId = $"{_tag}_contract";
        _ids.AddRange(new[] { fileId, blockId, contractId });

        var file = TestDataBuilder.CreateFileMetadata(fileId: fileId, type: "post");
        _files!.Upsert(file);
        _files.LoadAll()[fileId].Type.Should().Be("post");

        var block = TestDataBuilder.CreateBlockMetadata(blockId: blockId, blockIndex: 7);
        _blocks!.Upsert(block);
        _blocks.LoadAll()[blockId].BlockIndex.Should().Be(7);

        var contract = TestDataBuilder.CreateStorageContract(contractId: contractId);
        _contracts!.Upsert(contract);
        _contracts.LoadAll().Should().ContainKey(contractId);

        contract.Status = "Closed";
        _contracts.Upsert(contract);
        _contracts.LoadAll()[contractId].Status.Should().Be("Closed");

        _files.Delete(fileId);
        _blocks.Delete(blockId);
        _files.LoadAll().Should().NotContainKey(fileId);
        _blocks.LoadAll().Should().NotContainKey(blockId);
    }

    private sealed class SimpleFactory : IDbContextFactory<StoreDbContext>
    {
        private readonly DbContextOptions<StoreDbContext> _options;
        public SimpleFactory(DbContextOptions<StoreDbContext> options) => _options = options;
        public StoreDbContext CreateDbContext() => new(_options);
    }
}
