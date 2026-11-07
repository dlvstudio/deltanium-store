using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeltaniumStore.Models;
using Microsoft.EntityFrameworkCore;

namespace DeltaniumStore.Data
{
    public class MySqlFileMetadataStore : IFileMetadataStore
    {
        private readonly IDbContextFactory<StoreDbContext> _factory;
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public MySqlFileMetadataStore(IDbContextFactory<StoreDbContext> factory)
        {
            _factory = factory;
        }

        public Dictionary<string, FileMetadata> LoadAll()
        {
            using var db = _factory.CreateDbContext();
            var result = new Dictionary<string, FileMetadata>();
            foreach (var record in db.FileMetadataRecords.AsNoTracking())
            {
                var metadata = JsonSerializer.Deserialize<FileMetadata>(record.PayloadJson, Options);
                if (metadata != null && !string.IsNullOrEmpty(metadata.FileId))
                    result[metadata.FileId] = metadata;
            }
            return result;
        }

        public void Upsert(FileMetadata metadata)
        {
            using var db = _factory.CreateDbContext();
            var json = JsonSerializer.Serialize(metadata, Options);
            var existing = db.FileMetadataRecords.FirstOrDefault(r => r.Id == metadata.FileId);
            if (existing == null)
            {
                db.FileMetadataRecords.Add(new FileMetadataRecord
                {
                    Id = metadata.FileId,
                    PayloadJson = json,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.PayloadJson = json;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            db.SaveChanges();
        }

        public void Delete(string fileId)
        {
            using var db = _factory.CreateDbContext();
            var existing = db.FileMetadataRecords.FirstOrDefault(r => r.Id == fileId);
            if (existing == null) return;
            db.FileMetadataRecords.Remove(existing);
            db.SaveChanges();
        }
    }

    public class MySqlBlockMetadataStore : IBlockMetadataStore
    {
        private readonly IDbContextFactory<StoreDbContext> _factory;
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public MySqlBlockMetadataStore(IDbContextFactory<StoreDbContext> factory)
        {
            _factory = factory;
        }

        public Dictionary<string, BlockMetadata> LoadAll()
        {
            using var db = _factory.CreateDbContext();
            var result = new Dictionary<string, BlockMetadata>();
            foreach (var record in db.BlockMetadataRecords.AsNoTracking())
            {
                var metadata = JsonSerializer.Deserialize<BlockMetadata>(record.PayloadJson, Options);
                if (metadata != null && !string.IsNullOrEmpty(metadata.BlockId))
                    result[metadata.BlockId] = metadata;
            }
            return result;
        }

        public void Upsert(BlockMetadata metadata)
        {
            using var db = _factory.CreateDbContext();
            var json = JsonSerializer.Serialize(metadata);
            var existing = db.BlockMetadataRecords.FirstOrDefault(r => r.Id == metadata.BlockId);
            if (existing == null)
            {
                db.BlockMetadataRecords.Add(new BlockMetadataRecord
                {
                    Id = metadata.BlockId,
                    PayloadJson = json,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.PayloadJson = json;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            db.SaveChanges();
        }

        public void Delete(string blockId)
        {
            using var db = _factory.CreateDbContext();
            var existing = db.BlockMetadataRecords.FirstOrDefault(r => r.Id == blockId);
            if (existing == null) return;
            db.BlockMetadataRecords.Remove(existing);
            db.SaveChanges();
        }
    }

    public class MySqlContractStore : IContractStore
    {
        private readonly IDbContextFactory<StoreDbContext> _factory;
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public MySqlContractStore(IDbContextFactory<StoreDbContext> factory)
        {
            _factory = factory;
        }

        public Dictionary<string, StorageContract> LoadAll()
        {
            using var db = _factory.CreateDbContext();
            var result = new Dictionary<string, StorageContract>();
            foreach (var record in db.StorageContractRecords.AsNoTracking())
            {
                var contract = JsonSerializer.Deserialize<StorageContract>(record.PayloadJson, Options);
                if (contract != null && !string.IsNullOrEmpty(contract.ContractId))
                    result[contract.ContractId] = contract;
            }
            return result;
        }

        public void Upsert(StorageContract contract)
        {
            using var db = _factory.CreateDbContext();
            var json = JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = false });
            var existing = db.StorageContractRecords.FirstOrDefault(r => r.Id == contract.ContractId);
            if (existing == null)
            {
                db.StorageContractRecords.Add(new StorageContractRecord
                {
                    Id = contract.ContractId,
                    PayloadJson = json,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.PayloadJson = json;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            db.SaveChanges();
        }
    }
}
