using System;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DeltaniumStore.Data
{
    public class StoreDbContext : DbContext
    {
        public StoreDbContext(DbContextOptions<StoreDbContext> options) : this(options, "")
        {
        }

        /// <summary>
        /// <paramref name="tablePrefix"/> keeps each Store's rows in its own tables when several Stores share one
        /// MySQL database ("" = the original shared tables). Letters, digits and '_' only.
        /// </summary>
        public StoreDbContext(DbContextOptions<StoreDbContext> options, string tablePrefix) : base(options)
        {
            TablePrefix = ValidatePrefix(tablePrefix);
        }

        public string TablePrefix { get; }

        public static string ValidatePrefix(string? prefix)
        {
            prefix ??= "";
            if (prefix.Length > 32 || !Regex.IsMatch(prefix, "^[A-Za-z0-9_]*$"))
                throw new ArgumentException("Storage:MetadataTablePrefix may only contain letters, digits and '_' (max 32).");
            return prefix;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // EF caches one model per context type; key it by prefix so prefixed and legacy contexts can coexist.
            optionsBuilder.ReplaceService<IModelCacheKeyFactory, PrefixModelCacheKeyFactory>();
        }

        public DbSet<FileMetadataRecord> FileMetadataRecords => Set<FileMetadataRecord>();
        public DbSet<BlockMetadataRecord> BlockMetadataRecords => Set<BlockMetadataRecord>();
        public DbSet<StorageContractRecord> StorageContractRecords => Set<StorageContractRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Configure(modelBuilder.Entity<FileMetadataRecord>(), TablePrefix + "file_metadata");
            Configure(modelBuilder.Entity<BlockMetadataRecord>(), TablePrefix + "block_metadata");
            Configure(modelBuilder.Entity<StorageContractRecord>(), TablePrefix + "storage_contracts");
        }

        private static void Configure<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e, string table)
            where T : class, IJsonPayloadRecord
        {
            e.ToTable(table);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.PayloadJson).HasColumnType("longtext").IsRequired();
        }
    }

    public sealed class PrefixModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), (context as StoreDbContext)?.TablePrefix ?? "", designTime);
    }

    /// <summary>Creates <see cref="StoreDbContext"/>s that use one table prefix.</summary>
    public sealed class PrefixedStoreDbContextFactory : IDbContextFactory<StoreDbContext>
    {
        private readonly DbContextOptions<StoreDbContext> _options;
        public PrefixedStoreDbContextFactory(DbContextOptions<StoreDbContext> options, string tablePrefix)
        {
            _options = options;
            TablePrefix = StoreDbContext.ValidatePrefix(tablePrefix);
        }
        public string TablePrefix { get; }
        public StoreDbContext CreateDbContext() => new(_options, TablePrefix);
    }

    public interface IJsonPayloadRecord
    {
        string Id { get; set; }
        string PayloadJson { get; set; }
        DateTime UpdatedAt { get; set; }
    }

    public class FileMetadataRecord : IJsonPayloadRecord
    {
        public string Id { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class BlockMetadataRecord : IJsonPayloadRecord
    {
        public string Id { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class StorageContractRecord : IJsonPayloadRecord
    {
        public string Id { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
