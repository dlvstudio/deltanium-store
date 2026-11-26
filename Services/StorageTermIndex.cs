using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DeltaniumStore.Security;
using Microsoft.Extensions.Configuration;

namespace DeltaniumStore.Services
{
    /// <summary>One StorageTerm tx as the chain returns it.</summary>
    public record StorageTermTx(string Id, string From, long Timestamp, string Signature, long BlockHeight, string Terms);

    /// <summary>Reads confirmed StorageTerm txs from a Blocker (oldest first, above a height).</summary>
    public interface IStorageTermSource
    {
        /// <summary>Null when the Blocker cannot be reached.</summary>
        Task<List<StorageTermTx>?> FetchAsync(long afterHeight, int limit);
    }

    public class BlockerStorageTermSource : IStorageTermSource
    {
        private readonly HttpClient _http;
        private readonly string _blockerApi;

        public BlockerStorageTermSource(HttpClient http, IConfiguration config)
        {
            _http = http;
            _http.Timeout = TimeSpan.FromSeconds(30);
            _blockerApi = (config["Blockchain:BlockerApi"] ?? "http://localhost:5003").TrimEnd('/');
        }

        public async Task<List<StorageTermTx>?> FetchAsync(long afterHeight, int limit)
        {
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                    $"{_blockerApi}/api/explorer/storage-terms?afterHeight={afterHeight}&limit={limit}"));
                var list = new List<StorageTermTx>();
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
                foreach (var it in items.EnumerateArray())
                {
                    string S(string n) => it.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    long L(string n) => it.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : 0;
                    var terms = it.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                                d.TryGetProperty("terms", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    list.Add(new StorageTermTx(S("id"), S("from"), L("timestamp"), S("signature"), L("blockHeight"), terms));
                }
                return list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageTerms] Blocker unreachable: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// What the chain says about each stored item: paid until when and from when it may be deleted. Built only from
    /// StorageTerm txs whose Central signature this Store checked itself; nothing is taken from Central's API.
    /// Persisted in &lt;storage&gt;/storage_terms.json with the last block height read.
    /// </summary>
    public class StorageTermIndex
    {
        public class Term
        {
            [JsonPropertyName("status")] public string Status { get; set; } = "";
            [JsonPropertyName("paidUntil")] public long PaidUntil { get; set; }
            [JsonPropertyName("deleteAfter")] public long DeleteAfter { get; set; }
            [JsonPropertyName("blocks")] public List<string>? Blocks { get; set; }
            [JsonPropertyName("height")] public long Height { get; set; }
            [JsonPropertyName("ts")] public long Timestamp { get; set; }
            [JsonPropertyName("deleted")] public bool Deleted { get; set; }
        }

        private class State
        {
            public long AfterHeight { get; set; } = -1;
            public Dictionary<string, Term> Items { get; set; } = new();
        }

        private sealed record Entry(
            [property: JsonPropertyName("fileId")] string FileId,
            [property: JsonPropertyName("status")] string Status,
            [property: JsonPropertyName("paidUntil")] long PaidUntil,
            [property: JsonPropertyName("deleteAfter")] long DeleteAfter,
            [property: JsonPropertyName("blocks")] List<string>? Blocks);

        private readonly string _path;
        private readonly object _gate = new();
        private State _state;

        public StorageTermIndex(string storageBasePath)
        {
            _path = Path.Combine(storageBasePath, "storage_terms.json");
            _state = Load();
        }

        public long AfterHeight { get { lock (_gate) return _state.AfterHeight; } }

        public Term? Get(string fileId) { lock (_gate) return _state.Items.TryGetValue(fileId, out var t) ? t : null; }

        public static string SignPayload(string id, long timestamp, string terms) =>
            $"STORAGETERM|{id}|{timestamp}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms))).ToLowerInvariant()}";

        /// <summary>A tx counts only if Central (by its public key) signed exactly this id, time and terms.</summary>
        public static bool IsAuthentic(StorageTermTx tx, string centralPublicKey) =>
            string.Equals(tx.From, centralPublicKey, StringComparison.OrdinalIgnoreCase) &&
            tx.Terms.Length > 0 &&
            CentralRequestAuth.Verify(centralPublicKey, NetworkScope.Scope(SignPayload(tx.Id, tx.Timestamp, tx.Terms)), tx.Signature);

        /// <summary>Reads new StorageTerm txs from the chain. Returns (txs read, txs rejected) or null when unreachable.</summary>
        public async Task<(int Read, int Rejected)?> SyncAsync(IStorageTermSource source, string centralPublicKey, int maxPages = 50)
        {
            int read = 0, rejected = 0;
            for (var page = 0; page < maxPages; page++)
            {
                var txs = await source.FetchAsync(AfterHeight, 100);
                if (txs == null) return read == 0 ? null : (read, rejected);
                if (txs.Count == 0) break;
                lock (_gate)
                {
                    foreach (var tx in txs)
                    {
                        read++;
                        if (!IsAuthentic(tx, centralPublicKey)) { rejected++; continue; }
                        List<Entry> entries;
                        try { entries = JsonSerializer.Deserialize<List<Entry>>(tx.Terms) ?? new(); }
                        catch (JsonException) { rejected++; continue; }
                        foreach (var e in entries.Where(e => !string.IsNullOrWhiteSpace(e.FileId)))
                            Apply(e, tx.BlockHeight, tx.Timestamp);
                    }
                    _state.AfterHeight = Math.Max(_state.AfterHeight, txs.Max(t => t.BlockHeight));
                    Save();
                }
            }
            return (read, rejected);
        }

        private void Apply(Entry e, long height, long ts)
        {
            _state.Items.TryGetValue(e.FileId, out var cur);
            if (cur != null && (cur.Height > height || (cur.Height == height && cur.Timestamp > ts))) return; // older news
            _state.Items[e.FileId] = new Term
            {
                Status = e.Status,
                PaidUntil = e.PaidUntil,
                DeleteAfter = e.DeleteAfter,
                Blocks = e.Blocks ?? cur?.Blocks, // block ids are published once
                Height = height,
                Timestamp = ts,
                // Paid again after a deletion would be a Central bug; the data is gone either way.
                Deleted = cur?.Deleted == true,
            };
        }

        /// <summary>Items the chain says may now be deleted and that this Store has not deleted yet.</summary>
        public List<(string FileId, Term Term)> Due(long nowUnix)
        {
            lock (_gate)
                return _state.Items.Where(kv => !kv.Value.Deleted && kv.Value.DeleteAfter > 0 && kv.Value.DeleteAfter <= nowUnix)
                    .Select(kv => (kv.Key, kv.Value)).ToList();
        }

        public void MarkDeleted(string fileId)
        {
            lock (_gate)
            {
                if (_state.Items.TryGetValue(fileId, out var t)) t.Deleted = true;
                Save();
            }
        }

        private State Load()
        {
            try { return File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State() : new State(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageTerms] {_path} unreadable, re-reading the chain: {ex.Message}");
                return new State();
            }
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
