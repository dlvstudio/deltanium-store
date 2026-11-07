using System.Collections.Generic;
using DeltaniumStore.Models;

namespace DeltaniumStore.Data
{
    public interface IFileMetadataStore
    {
        Dictionary<string, FileMetadata> LoadAll();
        void Upsert(FileMetadata metadata);
        void Delete(string fileId);
    }

    public interface IBlockMetadataStore
    {
        Dictionary<string, BlockMetadata> LoadAll();
        void Upsert(BlockMetadata metadata);
        void Delete(string blockId);
    }

    public interface IContractStore
    {
        Dictionary<string, StorageContract> LoadAll();
        void Upsert(StorageContract contract);
    }
}
