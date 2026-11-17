using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeltaniumStore.Data
{
    /// <summary>
    /// One-off copy from the metadata tables several Stores used to share into this Store's own (prefixed) tables.
    /// Only rows for blocks this Store actually holds on disk are copied, so a Store never inherits another
    /// Store's entries (which is what let one Store's prune delete the other's files).
    /// </summary>
    public static class MetadataScopeMigration
    {
        public sealed record Result(int Files, int Blocks, int Contracts);

        public static Result CopyLocalRows(
            IFileMetadataStore fromFiles, IBlockMetadataStore fromBlocks, IContractStore fromContracts,
            IFileMetadataStore toFiles, IBlockMetadataStore toBlocks, IContractStore toContracts,
            string blockDataPath)
        {
            bool Local(string blockId) =>
                !string.IsNullOrEmpty(blockId) && File.Exists(Path.Combine(blockDataPath, $"{blockId}.data"));

            var blocks = 0;
            foreach (var b in fromBlocks.LoadAll().Values.Where(b => Local(b.BlockId)))
            {
                toBlocks.Upsert(b);
                blocks++;
            }

            var fileIds = new HashSet<string>();
            foreach (var f in fromFiles.LoadAll().Values.Where(f => Local(f.FirstBlockId)))
            {
                toFiles.Upsert(f);
                fileIds.Add(f.FileId);
            }

            var contracts = 0;
            foreach (var c in fromContracts.LoadAll().Values
                         .Where(c => c.FileIds.Count == 0 || c.FileIds.Any(fileIds.Contains)))
            {
                toContracts.Upsert(c);
                contracts++;
            }
            return new Result(fileIds.Count, blocks, contracts);
        }
    }
}
