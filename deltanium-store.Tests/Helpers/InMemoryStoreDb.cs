using DeltaniumStore.Data;
using Microsoft.EntityFrameworkCore;

namespace deltanium_store.Tests.Helpers;

public sealed class InMemoryStoreDbFactory : IDbContextFactory<StoreDbContext>, IDisposable
{
    private readonly DbContextOptions<StoreDbContext> _options;

    public InMemoryStoreDbFactory(string? name = null)
    {
        _options = new DbContextOptionsBuilder<StoreDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N"))
            .Options;

        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public StoreDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
    }
}
