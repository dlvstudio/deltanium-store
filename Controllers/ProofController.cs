using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DeltaniumStore.Controllers
{
    /// <summary>
    /// Central-only endpoints for TOKENOMICS_V2: proof-of-storage challenges (§6) and egress reports (§7).
    /// Authenticated by X-Internal-Token (shared INTERNAL_API_TOKEN), not user signatures.
    /// </summary>
    [ApiController]
    [Route("api/proof")]
    public class ProofController : ControllerBase
    {
        private readonly IFileStorageService _files;
        private readonly IEgressTracker _egress;
        private readonly IConfiguration _config;

        public ProofController(IFileStorageService files, IEgressTracker egress, IConfiguration config)
        {
            _files = files;
            _egress = egress;
            _config = config;
        }

        public class ChallengeRequest
        {
            public string BlockId { get; set; } = "";
            public string Nonce { get; set; } = "";
        }

        /// <summary>True when CentralRequestAuth verified the Central signature on this request.</summary>
        public static bool IsInternal(Microsoft.AspNetCore.Http.HttpRequest request, IConfiguration config) =>
            DeltaniumStore.Security.CentralRequestAuth.IsAuthenticated(request.HttpContext);

        /// <summary>Return the full block so Central can hash it against the hash registered at upload.</summary>
        [HttpPost("challenge")]
        public async Task<IActionResult> Challenge([FromBody] ChallengeRequest req)
        {
            if (!IsInternal(Request, _config)) return Unauthorized("Central signature required");
            if (string.IsNullOrWhiteSpace(req.BlockId) || string.IsNullOrWhiteSpace(req.Nonce))
                return BadRequest("blockId and nonce required");
            try
            {
                await using var stream = await _files.GetBlockAsync(req.BlockId);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var blockMeta = await _files.GetBlockMetadataAsync(req.BlockId);
                return Ok(new
                {
                    blockId = req.BlockId,
                    blockIndex = blockMeta?.BlockIndex ?? -1,
                    nonce = req.Nonce,
                    size = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    dataBase64 = Convert.ToBase64String(bytes)
                });
            }
            catch (FileNotFoundException)
            {
                return NotFound("Block not found");
            }
        }

        /// <summary>Hand over and reset per-file egress counters plus queued reader receipts.</summary>
        [HttpPost("egress/drain")]
        public IActionResult DrainEgress()
        {
            if (!IsInternal(Request, _config)) return Unauthorized("Central signature required");
            var items = _egress.Drain();
            var receipts = new List<JsonElement>();
            foreach (var raw in _egress.DrainReceipts())
            {
                try { using var d = JsonDocument.Parse(raw); receipts.Add(d.RootElement.Clone()); }
                catch (JsonException) { /* dropped: malformed */ }
            }
            return Ok(new
            {
                items = items.Select(i => new { fileId = i.FileId, bytes = i.Bytes }),
                receipts,
                drainedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Reader-signed bandwidth receipt (§7). Public: the reader's signature is verified by Central, which
        /// only bills bytes that readers signed for when Economics:Bandwidth:ReceiptMode=require.
        /// </summary>
        [HttpPost("egress/receipt")]
        [RequestSizeLimit(512 * 1024)]
        public IActionResult AddReceipt([FromBody] JsonElement receipt)
        {
            if (receipt.ValueKind != JsonValueKind.Object ||
                !receipt.TryGetProperty("signature", out var sig) || string.IsNullOrWhiteSpace(sig.GetString()) ||
                !receipt.TryGetProperty("reader", out var reader) || string.IsNullOrWhiteSpace(reader.GetString()) ||
                !receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0 || items.GetArrayLength() > 5000)
                return BadRequest("reader, signature and 1-5000 items required");
            return _egress.AddReceipt(receipt.GetRawText()) ? Ok(new { queued = true }) : StatusCode(503, "Receipt queue full");
        }

        public class ExportRequest { public string FileId { get; set; } = ""; }

        /// <summary>Metadata rows of a file, for Central to copy onto a replacement Store (§6 re-replication).</summary>
        [HttpPost("export-metadata")]
        public async Task<IActionResult> ExportMetadata([FromBody] ExportRequest req)
        {
            if (!IsInternal(Request, _config)) return Unauthorized("Central signature required");
            if (string.IsNullOrWhiteSpace(req.FileId)) return BadRequest("fileId required");
            return Ok(new { items = await _files.GetAllMetadataForFileAsync(req.FileId) });
        }

        public class ImportMetadataRequest { public List<FileMetadata> Items { get; set; } = new(); }

        [HttpPost("import-metadata")]
        public async Task<IActionResult> ImportMetadata([FromBody] ImportMetadataRequest req)
        {
            if (!IsInternal(Request, _config)) return Unauthorized("Central signature required");
            if (req.Items.Count == 0) return BadRequest("items required");
            foreach (var m in req.Items.Where(m => !string.IsNullOrWhiteSpace(m.FileId)))
                await _files.StoreFileMetadataAsync(m);
            return Ok(new { imported = req.Items.Count });
        }

        public class ImportBlockRequest
        {
            public string FileId { get; set; } = "";
            public string BlockId { get; set; } = "";
            public int BlockIndex { get; set; }
            public string DataBase64 { get; set; } = "";
            public string Sha256 { get; set; } = "";
        }

        /// <summary>Store one encrypted block copied by Central from a healthy replica; hash must match.</summary>
        [HttpPost("import-block")]
        [RequestSizeLimit(64 * 1024 * 1024)]
        public async Task<IActionResult> ImportBlock([FromBody] ImportBlockRequest req)
        {
            if (!IsInternal(Request, _config)) return Unauthorized("Central signature required");
            if (string.IsNullOrWhiteSpace(req.BlockId) || string.IsNullOrWhiteSpace(req.DataBase64))
                return BadRequest("blockId and dataBase64 required");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(req.DataBase64); }
            catch (FormatException) { return BadRequest("dataBase64 invalid"); }
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(req.Sha256) && !string.Equals(hash, req.Sha256, StringComparison.OrdinalIgnoreCase))
                return BadRequest("sha256 mismatch");
            using var ms = new MemoryStream(bytes);
            await _files.StoreBlockAsync(ms, req.BlockId, req.BlockIndex);
            if (!string.IsNullOrWhiteSpace(req.FileId)) _egress.MapBlock(req.BlockId, req.FileId);
            return Ok(new { blockId = req.BlockId, size = bytes.Length, sha256 = hash });
        }
    }
}
