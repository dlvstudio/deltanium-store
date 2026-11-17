using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Counts bytes served per owner fileId (TOKENOMICS_V2 §7). Central drains the counters periodically
    /// and bills bandwidth over quota. Block→file mapping is learned from the X-File-Id header at upload.
    /// </summary>
    public interface IEgressTracker
    {
        void MapBlock(string blockId, string fileId);
        void RecordBlockServed(string blockId, long bytes);
        void RecordFileServed(string fileId, long bytes);
        List<(string FileId, long Bytes)> Drain();
        /// <summary>Queue a reader-signed receipt (raw JSON) for Central; false when the queue is full.</summary>
        bool AddReceipt(string receiptJson);
        List<string> DrainReceipts();
    }

    public class EgressTracker : IEgressTracker
    {
        private readonly string _mapPath;
        private readonly string _countPath;
        private readonly object _gate = new();
        private readonly ConcurrentDictionary<string, string> _blockToFile;
        private Dictionary<string, long> _counts;
        private readonly string _receiptPath;
        private List<string> _receipts;
        public const int MaxQueuedReceipts = 100_000;

        public EgressTracker(string storageBasePath)
        {
            Directory.CreateDirectory(storageBasePath);
            _mapPath = Path.Combine(storageBasePath, "egress_block_map.json");
            _countPath = Path.Combine(storageBasePath, "egress_counts.json");
            _blockToFile = new ConcurrentDictionary<string, string>(
                Load<Dictionary<string, string>>(_mapPath) ?? new Dictionary<string, string>());
            _counts = Load<Dictionary<string, long>>(_countPath) ?? new Dictionary<string, long>();
            _receiptPath = Path.Combine(storageBasePath, "egress_receipts.json");
            _receipts = Load<List<string>>(_receiptPath) ?? new List<string>();
        }

        public bool AddReceipt(string receiptJson)
        {
            lock (_gate)
            {
                if (_receipts.Count >= MaxQueuedReceipts) return false;
                _receipts.Add(receiptJson);
                File.WriteAllText(_receiptPath, JsonConvert.SerializeObject(_receipts));
                return true;
            }
        }

        public List<string> DrainReceipts()
        {
            lock (_gate)
            {
                var items = _receipts;
                _receipts = new List<string>();
                File.WriteAllText(_receiptPath, "[]");
                return items;
            }
        }

        private static T? Load<T>(string path)
        {
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) : default; }
            catch { return default; }
        }

        public void MapBlock(string blockId, string fileId)
        {
            if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(fileId)) return;
            _blockToFile[blockId] = fileId;
            lock (_gate) File.WriteAllText(_mapPath, JsonConvert.SerializeObject(_blockToFile));
        }

        public void RecordBlockServed(string blockId, long bytes)
        {
            if (_blockToFile.TryGetValue(blockId, out var fileId)) RecordFileServed(fileId, bytes);
        }

        public void RecordFileServed(string fileId, long bytes)
        {
            if (bytes <= 0) return;
            lock (_gate)
            {
                _counts[fileId] = (_counts.TryGetValue(fileId, out var c) ? c : 0) + bytes;
                File.WriteAllText(_countPath, JsonConvert.SerializeObject(_counts));
            }
        }

        public List<(string FileId, long Bytes)> Drain()
        {
            lock (_gate)
            {
                var items = _counts.Select(kv => (kv.Key, kv.Value)).ToList();
                _counts = new Dictionary<string, long>();
                File.WriteAllText(_countPath, "{}");
                return items;
            }
        }
    }
}
