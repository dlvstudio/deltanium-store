using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumStore;
using DeltaniumStore.Security;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_store.Tests.Services
{
    public class EndedStorageCleanupTests : System.IDisposable
    {
        private readonly TestFileHelper _dir = new();
        private readonly FileStorageService _files;
        private readonly StorageTermIndex _index;
        private readonly Key _central = new();
        private string CentralPub => _central.PubKey.ToHex();
        private readonly Mock<ICentralApiService> _centralApi = new();

        public EndedStorageCleanupTests()
        {
            _files = new FileStorageService(_dir.TempDirectory);
            _index = new StorageTermIndex(_dir.TempDirectory);
        }

        public void Dispose() => _dir.Dispose();

        private class FakeChain : IStorageTermSource
        {
            public List<StorageTermTx> Txs { get; } = new();
            public bool Down { get; set; }
            public Task<List<StorageTermTx>?> FetchAsync(long afterHeight, int limit) =>
                Task.FromResult(Down ? null : Txs.Where(t => t.BlockHeight > afterHeight).OrderBy(t => t.BlockHeight).Take(limit).ToList());
        }

        private StorageTermTx Tx(Key signer, string id, long height, params object[] entries)
        {
            var terms = JsonSerializer.Serialize(entries);
            var payload = StorageTermIndex.SignPayload(id, 1_800_000_000 + height, terms);
            var sig = System.Convert.ToBase64String(signer.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(payload))))).ToDER());
            return new StorageTermTx(id, signer.PubKey.ToHex(), 1_800_000_000 + height, sig, height, terms);
        }

        private static object Term(string fileId, string status, long deleteAfter, string[]? blocks = null) =>
            blocks == null
                ? new { fileId, status, paidUntil = deleteAfter - 100, deleteAfter }
                : new { fileId, status, paidUntil = deleteAfter - 100, deleteAfter, blocks };

        private async Task Entry(string fileId, string firstBlockId, string? parent = null)
        {
            var m = TestDataBuilder.CreateFileMetadata();
            m.FileId = fileId;
            m.FirstBlockId = firstBlockId;
            m.ParentFileId = parent;
            await _files.StoreFileMetadataAsync(m);
            await _files.StoreBlockAsync(new MemoryStream(new byte[] { 1, 2, 3 }), firstBlockId, 0);
        }

        private bool BlockOnDisk(string id) => File.Exists(Path.Combine(_dir.TempDirectory, "block_data", id + ".data"));

        private EndedStorageCleanupService Service() => new(null!, _files, _index, new ConfigurationBuilder().Build(),
            NullLogger<EndedStorageCleanupService>.Instance);

        [Fact]
        public async Task Items_are_deleted_when_the_chain_says_their_term_ran_out_and_not_before()
        {
            await Entry("post-me", "post-b0-me");
            await Entry("post-f1", "post-b0-f1");
            await _files.StoreBlockAsync(new MemoryStream(new byte[] { 9 }), "post-c1", 1);
            await Entry("like-bob", "like-b0", parent: "post-me");
            await Entry("kept", "kept-b0");
            var chain = new FakeChain();
            // Registered with its blocks, then renewed (no blocks), then ended at 2000.
            chain.Txs.Add(Tx(_central, "st-1", 1, Term("post", "active", 1000, new[] { "post-b0-me", "post-b0-f1", "post-c1" }),
                Term("kept", "active", 5000)));
            chain.Txs.Add(Tx(_central, "st-2", 2, Term("post", "active", 1500)));
            chain.Txs.Add(Tx(_central, "st-3", 3, Term("post", "ended", 2000)));
            var svc = Service();

            var early = await svc.RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: 1999);
            early.Read.Should().Be(3);
            early.Items.Should().Be(0);
            (await _files.FileExistsAsync("post-me")).Should().BeTrue();

            var due = await svc.RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: 2000);
            due.Should().Be(new EndedStorageCleanupService.PassResult(0, 0, 1, 2, 3, 1));
            (await _files.FileExistsAsync("post-me")).Should().BeFalse();
            (await _files.FileExistsAsync("post-f1")).Should().BeFalse();
            BlockOnDisk("post-c1").Should().BeFalse();
            (await _files.FileExistsAsync("like-bob")).Should().BeTrue();   // own term: reported, not deleted
            (await _files.FileExistsAsync("kept")).Should().BeTrue();
            _centralApi.Verify(c => c.ReportChildrenEndedAsync("post", It.IsAny<IReadOnlyCollection<string>>()), Times.Once);

            (await svc.RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: 3000)).Items.Should().Be(0); // once only
        }

        [Fact]
        public async Task Terms_not_signed_by_Central_are_ignored()
        {
            await Entry("victim", "victim-b0");
            var chain = new FakeChain();
            chain.Txs.Add(Tx(new Key(), "st-forged", 1, Term("victim", "ended", 10, new[] { "victim-b0" })));
            var forgedFrom = Tx(new Key(), "st-forged-2", 2, Term("victim", "ended", 10));
            chain.Txs.Add(forgedFrom with { From = CentralPub });

            var r = await Service().RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: 100);
            r.Rejected.Should().Be(2);
            r.Items.Should().Be(0);
            (await _files.FileExistsAsync("victim")).Should().BeTrue();
        }

        [Fact]
        public async Task Nothing_is_deleted_without_a_term_on_chain_or_when_the_chain_is_unreachable()
        {
            await Entry("legacy", "legacy-b0");
            var chain = new FakeChain { Down = true };
            (await Service().RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: long.MaxValue / 2)).Items.Should().Be(0);
            chain.Down = false;
            (await Service().RunOnceAsync(chain, CentralPub, _centralApi.Object, nowUnix: long.MaxValue / 2)).Items.Should().Be(0);
            (await _files.FileExistsAsync("legacy")).Should().BeTrue();
        }

        [Fact]
        public async Task The_index_survives_a_restart()
        {
            var chain = new FakeChain();
            chain.Txs.Add(Tx(_central, "st-1", 4, Term("a", "active", 50, new[] { "a-b0" })));
            await _index.SyncAsync(chain, CentralPub);
            var reopened = new StorageTermIndex(_dir.TempDirectory);
            reopened.AfterHeight.Should().Be(4);
            reopened.Get("a")!.Blocks.Should().Equal("a-b0");
        }
    }
}
