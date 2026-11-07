using System;
using Microsoft.EntityFrameworkCore;

namespace DeltaniumStore.Data
{
    public class StoreDbContext : DbContext
    {
        public StoreDbContext(DbContextOptions<StoreDbContext> options) : base(options)
        {
        }

        public DbSet<FileMetadataRecord> FileMetadataRecords => Set<FileMetadataRecord>();
        public DbSet<BlockMetadataRecord> BlockMetadataRecords => Set<BlockMetadataRecord>();
        public DbSet<StorageContractRecord> StorageContractRecords => Set<StorageContractRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Configure(modelBuilder.Entity<FileMetadataRecord>(), "file_metadata");
            Configure(modelBuilder.Entity<BlockMetadataRecord>(), "block_metadata");
            Configure(modelBuilder.Entity<StorageContractRecord>(), "storage_contracts");
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
