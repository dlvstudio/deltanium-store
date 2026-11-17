using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumStore.Controllers;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeltaniumStore.Tests.Controllers
{
    /// <summary>Internal /api/proof endpoints: challenge, re-replication import/export, reader receipts.</summary>
    public class ProofControllerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "proof-" + Guid.NewGuid().ToString("N"));
        private readonly FileStorageService _files;
        private readonly EgressTracker _egress;

        public ProofControllerTests()
        {
            _files = new FileStorageService(_dir);
            _egress = new EgressTracker(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private ProofController Controller(bool internalToken = true)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Token"] = "tok" }).Build();
            var c = new ProofController(_files, _egress, config) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            // CentralRequestAuth (middleware) marks requests that carry a valid Central signature.
            if (internalToken) c.HttpContext.Items[DeltaniumStore.Security.CentralRequestAuth.ItemKey] = true;
            return c;
        }

        private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        private static T Prop<T>(object value, string name) =>
            JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.GetProperty(name).Deserialize<T>()!;

        [Fact]
        public async Task Imported_block_is_served_by_challenge_with_its_index()
        {
            var data = Encoding.UTF8.GetBytes("ciphertext");
            var import = await Controller().ImportBlock(new ProofController.ImportBlockRequest
            {
                FileId = "f1", BlockId = "b7", BlockIndex = 3, DataBase64 = Convert.ToBase64String(data), Sha256 = Hex(data)
            });
            import.Should().BeOfType<OkObjectResult>();

            var res = await Controller().Challenge(new ProofController.ChallengeRequest { BlockId = "b7", Nonce = "n1" });
            var ok = res.Should().BeOfType<OkObjectResult>().Subject.Value!;
            Prop<int>(ok, "blockIndex").Should().Be(3);
            Prop<string>(ok, "sha256").Should().Be(Hex(data));
            Prop<string>(ok, "nonce").Should().Be("n1");
        }

        [Fact]
        public async Task Import_rejects_hash_mismatch_and_missing_token()
        {
            var data = Encoding.UTF8.GetBytes("x");
            var bad = await Controller().ImportBlock(new ProofController.ImportBlockRequest
            {
                BlockId = "b1", DataBase64 = Convert.ToBase64String(data), Sha256 = new string('0', 64)
            });
            bad.Should().BeOfType<BadRequestObjectResult>();

            var noToken = await Controller(internalToken: false).ImportBlock(new ProofController.ImportBlockRequest
            {
                BlockId = "b1", DataBase64 = Convert.ToBase64String(data)
            });
            noToken.Should().BeOfType<UnauthorizedObjectResult>();
        }

        [Fact]
        public async Task Metadata_round_trips_between_stores()
        {
            await _files.StoreFileMetadataAsync(new FileMetadata { FileId = "f9", FirstBlockId = "b0", ShareType = "followers", PolicyTag = "subscribers:2026-11" });
            var export = await Controller().ExportMetadata(new ProofController.ExportRequest { FileId = "f9" });
            var items = Prop<List<FileMetadata>>(export.Should().BeOfType<OkObjectResult>().Subject.Value!, "items");
            items.Should().ContainSingle(m => m.FirstBlockId == "b0");

            var otherDir = _dir + "-dst";
            try
            {
                var dstFiles = new FileStorageService(otherDir);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Token"] = "tok" }).Build();
                var dst = new ProofController(dstFiles, new EgressTracker(otherDir), config) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                dst.HttpContext.Items[DeltaniumStore.Security.CentralRequestAuth.ItemKey] = true;
                (await dst.ImportMetadata(new ProofController.ImportMetadataRequest { Items = items })).Should().BeOfType<OkObjectResult>();
                (await dstFiles.GetAllMetadataForFileAsync("f9")).Should().ContainSingle(m => m.PolicyTag == "subscribers:2026-11");
            }
            finally { try { Directory.Delete(otherDir, true); } catch { } }
        }

        [Fact]
        public void Receipts_are_queued_and_drained_with_counters()
        {
            var receipt = JsonDocument.Parse("{\"reader\":\"pk\",\"origin\":\"http://s\",\"ts\":1,\"items\":[{\"block\":\"b1\",\"bytes\":10}],\"signature\":\"sig\"}").RootElement;
            Controller(internalToken: false).AddReceipt(receipt).Should().BeOfType<OkObjectResult>();
            Controller(internalToken: false).AddReceipt(JsonDocument.Parse("{\"reader\":\"pk\"}").RootElement)
                .Should().BeOfType<BadRequestObjectResult>();

            _egress.MapBlock("b1", "f1");
            _egress.RecordBlockServed("b1", 10);
            var drained = Controller().DrainEgress().Should().BeOfType<OkObjectResult>().Subject.Value!;
            Prop<List<JsonElement>>(drained, "receipts").Should().ContainSingle();
            _egress.DrainReceipts().Should().BeEmpty();
        }
    }
}
