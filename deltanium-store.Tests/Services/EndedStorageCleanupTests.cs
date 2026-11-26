using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace deltanium_store.Tests.Services
{
    public class EndedStorageCleanupTests : System.IDisposable
    {
        private readonly TestFileHelper _dir = new();
        private readonly FileStorageService _files;

        public EndedStorageCleanupTests() => _files = new FileStorageService(_dir.TempDirectory);

        public void Dispose() => _dir.Dispose();

        private async Task Entry(string fileId, string firstBlockId, string? parent = null, string? type = null)
        {
            var m = TestDataBuilder.CreateFileMetadata();
            m.FileId = fileId;
            m.FirstBlockId = firstBlockId;
            m.ParentFileId = parent;
            m.Type = type;
            await _files.StoreFileMetadataAsync(m);
            await _files.StoreBlockAsync(new MemoryStream(new byte[] { 1, 2, 3 }), firstBlockId, 0);
        }

        private bool BlockOnDisk(string id) => File.Exists(Path.Combine(_dir.TempDirectory, "block_data", id + ".data"));

        [Fact]
        public async Task An_ended_post_is_deleted_with_every_reader_entry_and_block_and_its_reactions_are_reported()
        {
            // A post: one metadata entry per reader, each with its own block 0, plus a shared content block.
            await Entry("post-me", "post-b0-me", type: "post");
            await Entry("post-f1", "post-b0-f1", type: "post");
            await _files.StoreBlockAsync(new MemoryStream(new byte[] { 9 }), "post-c1", 1);
            await Entry("like-bob", "like-b0", parent: "post-me", type: "react");
            await Entry("other-post", "other-b0", type: "post");

            var central = new Mock<ICentralApiService>();
            central.Setup(c => c.GetEndedItemsAsync()).ReturnsAsync(new List<EndedItem>
            {
                new("post", "Expired", new List<string> { "post-b0-me", "post-b0-f1", "post-c1", "../../etc" }),
            });
            var svc = new EndedStorageCleanupService(null!, _files, new ConfigurationBuilder().Build(),
                NullLogger<EndedStorageCleanupService>.Instance);

            var (items, entries, blocks, reported) = await svc.RunOnceAsync(central.Object);

            (items, entries, blocks, reported).Should().Be((1, 2, 3, 1));
            (await _files.FileExistsAsync("post-me")).Should().BeFalse();
            (await _files.FileExistsAsync("post-f1")).Should().BeFalse();
            BlockOnDisk("post-c1").Should().BeFalse();
            // The like has its own contract: kept until Central closes it, but reported so it stops renewing.
            (await _files.FileExistsAsync("like-bob")).Should().BeTrue();
            BlockOnDisk("like-b0").Should().BeTrue();
            (await _files.FileExistsAsync("other-post")).Should().BeTrue();
            central.Verify(c => c.ReportChildrenEndedAsync("post",
                It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ((List<string>)ids)[0] == "like-bob")), Times.Once);
        }

        [Fact]
        public async Task Nothing_is_deleted_when_Central_is_unreachable()
        {
            await Entry("post-me", "post-b0-me");
            var central = new Mock<ICentralApiService>();
            central.Setup(c => c.GetEndedItemsAsync()).ReturnsAsync((List<EndedItem>?)null);
            var svc = new EndedStorageCleanupService(null!, _files, new ConfigurationBuilder().Build(),
                NullLogger<EndedStorageCleanupService>.Instance);
            (await svc.RunOnceAsync(central.Object)).Should().Be((0, 0, 0, 0));
            (await _files.FileExistsAsync("post-me")).Should().BeTrue();
        }
    }
}
