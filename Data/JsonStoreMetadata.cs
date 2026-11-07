using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeltaniumStore.Models;

namespace DeltaniumStore.Data
{
    public class JsonFileMetadataStore : IFileMetadataStore
    {
        private readonly string _dir;
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public JsonFileMetadataStore(string storageBasePath)
        {
            _dir = Path.Combine(storageBasePath, "file_metadata");
            Directory.CreateDirectory(_dir);
        }

        public Dictionary<string, FileMetadata> LoadAll()
        {
            var result = new Dictionary<string, FileMetadata>();
            foreach (var file in Directory.GetFiles(_dir, "*.metadata.json"))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<FileMetadata>(File.ReadAllText(file), Options);
                    if (metadata != null && !string.IsNullOrEmpty(metadata.FileId))
                        result[metadata.FileId] = metadata;
                }
                catch
                {
                    // skip corrupt entries
                }
            }
            return result;
        }

        public void Upsert(FileMetadata metadata)
        {
            var path = Path.Combine(_dir, $"{metadata.FileId}.metadata.json");
            File.WriteAllText(path, JsonSerializer.Serialize(metadata, WriteOptions));
        }

        public void Delete(string fileId)
        {
            var path = Path.Combine(_dir, $"{fileId}.metadata.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public class JsonBlockMetadataStore : IBlockMetadataStore
    {
        private readonly string _dir;

        public JsonBlockMetadataStore(string storageBasePath)
        {
            _dir = Path.Combine(storageBasePath, "block_metadata");
            Directory.CreateDirectory(_dir);
        }

        public Dictionary<string, BlockMetadata> LoadAll()
        {
            var result = new Dictionary<string, BlockMetadata>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var file in Directory.GetFiles(_dir, "*.metadata.json"))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<BlockMetadata>(File.ReadAllText(file), options);
                    if (metadata != null && !string.IsNullOrEmpty(metadata.BlockId))
                        result[metadata.BlockId] = metadata;
                }
                catch
                {
                }
            }
            return result;
        }

        public void Upsert(BlockMetadata metadata)
        {
            var path = Path.Combine(_dir, $"{metadata.BlockId}.metadata.json");
            File.WriteAllText(path, JsonSerializer.Serialize(metadata));
        }

        public void Delete(string blockId)
        {
            var path = Path.Combine(_dir, $"{blockId}.metadata.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public class JsonContractStore : IContractStore
    {
        private readonly string _dir;

        public JsonContractStore(string storageBasePath)
        {
            _dir = Path.Combine(storageBasePath, "contracts");
            Directory.CreateDirectory(_dir);
        }

        public Dictionary<string, StorageContract> LoadAll()
        {
            var result = new Dictionary<string, StorageContract>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                try
                {
                    var contract = JsonSerializer.Deserialize<StorageContract>(File.ReadAllText(file), options);
                    if (contract != null && !string.IsNullOrEmpty(contract.ContractId))
                        result[contract.ContractId] = contract;
                }
                catch
                {
                }
            }
            return result;
        }

        public void Upsert(StorageContract contract)
        {
            var path = Path.Combine(_dir, $"{contract.ContractId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
