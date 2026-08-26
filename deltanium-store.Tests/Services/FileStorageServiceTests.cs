using FluentAssertions;
using Xunit;
using DeltaniumStore.Services;
using DeltaniumStore.Models;
using deltanium_store.Tests.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

namespace deltanium_store.Tests.Services
{
    public class FileStorageServiceTests : IDisposable
    {
        private readonly TestFileHelper _fileHelper;
        private readonly FileStorageService _fileStorage;

        public FileStorageServiceTests()
        {
            _fileHelper = new TestFileHelper();
            _fileStorage = new FileStorageService(_fileHelper.TempDirectory);
        }

        [Fact]
        public async Task StoreFileMetadataAsync_ShouldSaveNewFileMetadata()
        {
            // Arrange
            var metadata = TestDataBuilder.CreateFileMetadata();

            // Act
            var result = await _fileStorage.StoreFileMetadataAsync(metadata);

            // Assert
            result.Should().NotBeNull();
            result.FileId.Should().Be(metadata.FileId);
            
            // Verify file exists on disk
            var metadataPath = Path.Combine(_fileHelper.TempDirectory, "file_metadata", $"{metadata.FileId}.metadata.json");
            File.Exists(metadataPath).Should().BeTrue();
        }

        [Fact]
        public async Task StoreFileMetadataAsync_ShouldNormalizeRecipientPubKey()
        {
            // Arrange
            var uncompressedKey = TestDataBuilder.CreateUncompressedPublicKey();
            var metadata = TestDataBuilder.CreateFileMetadata(recipientPubKey: uncompressedKey);

            // Act
            var result = await _fileStorage.StoreFileMetadataAsync(metadata);

            // Assert
            result.RecipientPubKey.Length.Should().Be(66); // Should be compressed
            (result.RecipientPubKey.StartsWith("02") || result.RecipientPubKey.StartsWith("03")).Should().BeTrue();
        }

        [Fact]
        public async Task StoreFileMetadataAsync_ShouldUpdateCache()
        {
            // Arrange
            var metadata = TestDataBuilder.CreateFileMetadata();

            // Act
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Assert - Verify we can retrieve from cache
            var retrieved = await _fileStorage.GetFileMetadataAsync(metadata.FileId, metadata.RecipientPubKey);
            retrieved.Should().NotBeNull();
            retrieved.FileId.Should().Be(metadata.FileId);
        }

        [Fact]
        public async Task StoreFileMetadataAsync_ShouldSerializeWithCamelCase()
        {
            // Arrange
            var metadata = TestDataBuilder.CreateFileMetadata();

            // Act
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Assert - Verify JSON uses camelCase
            var metadataPath = Path.Combine(_fileHelper.TempDirectory, "file_metadata", $"{metadata.FileId}.metadata.json");
            var json = await File.ReadAllTextAsync(metadataPath);
            json.Should().Contain("fileId");
            json.Should().Contain("recipientPubKey");
        }

        [Fact]
        public async Task StoreBlockAsync_ShouldSaveBlockDataToDisk()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            var blockData = Encoding.UTF8.GetBytes("test block data");
            var stream = new MemoryStream(blockData);

            // Act
            var result = await _fileStorage.StoreBlockAsync(stream, blockId, 0);

            // Assert
            result.Should().NotBeNull();
            result.BlockId.Should().Be(blockId);
            
            // Verify block file exists
            var blockPath = Path.Combine(_fileHelper.TempDirectory, "block_data", $"{blockId}.data");
            File.Exists(blockPath).Should().BeTrue();
            
            var savedData = await File.ReadAllBytesAsync(blockPath);
            savedData.Should().BeEquivalentTo(blockData);
        }

        [Fact]
        public async Task StoreBlockAsync_ShouldCreateBlockMetadata()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            var blockData = Encoding.UTF8.GetBytes("test block data");
            var stream = new MemoryStream(blockData);

            // Act
            var result = await _fileStorage.StoreBlockAsync(stream, blockId, 0);

            // Assert
            result.BlockSize.Should().Be(blockData.Length);
            result.BlockIndex.Should().Be(0);
            result.BlockHash.Should().NotBeNullOrEmpty();
            
            // Verify metadata file exists
            var metadataPath = Path.Combine(_fileHelper.TempDirectory, "block_metadata", $"{blockId}.metadata.json");
            File.Exists(metadataPath).Should().BeTrue();
        }

        [Fact]
        public async Task StoreBlockAsync_ShouldCalculateBlockHash()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            var blockData = Encoding.UTF8.GetBytes("test block data");
            var stream = new MemoryStream(blockData);

            // Act
            var result = await _fileStorage.StoreBlockAsync(stream, blockId, 0);

            // Assert
            result.BlockHash.Should().NotBeNullOrEmpty();
            result.BlockHash.Length.Should().Be(64); // SHA256 hex string
        }

        [Fact]
        public async Task GetBlockAsync_ExistingBlock_ShouldReturnStream()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            var blockData = Encoding.UTF8.GetBytes("test block data");
            var stream = new MemoryStream(blockData);
            await _fileStorage.StoreBlockAsync(stream, blockId, 0);

            // Act
            var result = await _fileStorage.GetBlockAsync(blockId);

            // Assert
            result.Should().NotBeNull();
            using var reader = new StreamReader(result);
            var content = await reader.ReadToEndAsync();
            content.Should().Be("test block data");
        }

        [Fact]
        public async Task GetBlockAsync_NonExistentBlock_ShouldThrowFileNotFoundException()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();

            // Act & Assert
            await Assert.ThrowsAsync<FileNotFoundException>(() => _fileStorage.GetBlockAsync(blockId));
        }

        [Fact]
        public async Task GetFileMetadataAsync_ShouldReturnMetadataForMatchingRecipient()
        {
            // Arrange
            var (_, recipientKey) = TestDataBuilder.GenerateKeyPair();
            var metadata = TestDataBuilder.CreateFileMetadata(recipientPubKey: recipientKey);
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Act
            var result = await _fileStorage.GetFileMetadataAsync(metadata.FileId, recipientKey);

            // Assert
            result.Should().NotBeNull();
            result.FileId.Should().Be(metadata.FileId);
            result.RecipientPubKey.Should().Be(metadata.RecipientPubKey);
        }

        [Fact]
        public async Task GetFileMetadataAsync_PublicPost_ShouldReturnRegardlessOfRecipient()
        {
            // Arrange
            var (_, ownerKey) = TestDataBuilder.GenerateKeyPair();
            var (_, otherUserKey) = TestDataBuilder.GenerateKeyPair();
            var metadata = TestDataBuilder.CreateFileMetadata(
                recipientPubKey: ownerKey,
                isPublic: true,
                type: "post");
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Act
            var result = await _fileStorage.GetFileMetadataAsync(metadata.FileId, otherUserKey);

            // Assert
            result.Should().NotBeNull();
            result.FileId.Should().Be(metadata.FileId);
            result.IsPublic.Should().BeTrue();
        }

        [Fact]
        public async Task GetFileMetadataAsync_NonExistentFile_ShouldThrowFileNotFoundException()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();

            // Act & Assert
            await Assert.ThrowsAsync<FileNotFoundException>(() => 
                _fileStorage.GetFileMetadataAsync(fileId, userKey));
        }

        [Fact]
        public async Task ListUserFilesAsync_ShouldReturnAllFilesForUser()
        {
            // Arrange
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();
            var file1 = TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey);
            var file2 = TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey);
            await _fileStorage.StoreFileMetadataAsync(file1);
            await _fileStorage.StoreFileMetadataAsync(file2);

            // Act
            var result = await _fileStorage.ListUserFilesAsync(userKey);

            // Assert
            result.Should().HaveCountGreaterThanOrEqualTo(2);
            result.Should().Contain(f => f.FileId == file1.FileId);
            result.Should().Contain(f => f.FileId == file2.FileId);
        }

        [Fact]
        public async Task ListUserFilesAsync_ShouldNormalizePublicKey()
        {
            // Arrange
            var uncompressedKey = TestDataBuilder.CreateUncompressedPublicKey();
            var metadata = TestDataBuilder.CreateFileMetadata(recipientPubKey: uncompressedKey);
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Act - Search with uncompressed key
            var result = await _fileStorage.ListUserFilesAsync(uncompressedKey);

            // Assert
            result.Should().Contain(f => f.FileId == metadata.FileId);
        }

        [Fact]
        public async Task ListUserFilesAsync_EmptyResults_ShouldReturnEmptyList()
        {
            // Arrange
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();

            // Act
            var result = await _fileStorage.ListUserFilesAsync(userKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task FileExistsAsync_ExistingFile_ShouldReturnTrue()
        {
            // Arrange
            var metadata = TestDataBuilder.CreateFileMetadata();
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Act
            var result = await _fileStorage.FileExistsAsync(metadata.FileId);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task FileExistsAsync_NonExistentFile_ShouldReturnFalse()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();

            // Act
            var result = await _fileStorage.FileExistsAsync(fileId);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task ListAllPostsByShareTypeAsync_ShouldReturnPostsWithMatchingShareType()
        {
            // Arrange
            var post1 = TestDataBuilder.CreateFileMetadata(type: "post", shareType: "followers");
            var post2 = TestDataBuilder.CreateFileMetadata(type: "post", shareType: "followers");
            var file = TestDataBuilder.CreateFileMetadata(type: "file", shareType: "followers");
            await _fileStorage.StoreFileMetadataAsync(post1);
            await _fileStorage.StoreFileMetadataAsync(post2);
            await _fileStorage.StoreFileMetadataAsync(file);

            // Act
            var result = await _fileStorage.ListAllPostsByShareTypeAsync("followers");

            // Assert
            result.Should().HaveCount(2);
            result.All(p => p.Type == "post").Should().BeTrue();
            result.All(p => p.ShareType == "followers").Should().BeTrue();
        }

        [Fact]
        public async Task ListAllPublicPostsAsync_ShouldReturnAllPublicPosts()
        {
            // Arrange
            var publicPost1 = TestDataBuilder.CreateFileMetadata(isPublic: true, type: "post");
            var publicPost2 = TestDataBuilder.CreateFileMetadata(isPublic: true, type: "post");
            var privatePost = TestDataBuilder.CreateFileMetadata(isPublic: false, type: "post");
            await _fileStorage.StoreFileMetadataAsync(publicPost1);
            await _fileStorage.StoreFileMetadataAsync(publicPost2);
            await _fileStorage.StoreFileMetadataAsync(privatePost);

            // Act
            var result = await _fileStorage.ListAllPublicPostsAsync();

            // Assert
            result.Should().HaveCountGreaterThanOrEqualTo(2);
            result.All(p => p.IsPublic || p.EncryptedType == "public").Should().BeTrue();
        }

        [Fact]
        public async Task ListReactionsByParentAsync_ShouldReturnReactionsForParent()
        {
            // Arrange
            var parentFileId = Guid.NewGuid().ToString();
            var reaction1 = TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: parentFileId);
            var reaction2 = TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: parentFileId);
            var otherReaction = TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: Guid.NewGuid().ToString());
            await _fileStorage.StoreFileMetadataAsync(reaction1);
            await _fileStorage.StoreFileMetadataAsync(reaction2);
            await _fileStorage.StoreFileMetadataAsync(otherReaction);

            // Act
            var result = await _fileStorage.ListReactionsByParentAsync(parentFileId);

            // Assert
            result.Should().HaveCount(2);
            result.All(r => r.ParentFileId == parentFileId).Should().BeTrue();
        }

        [Fact]
        public async Task ListCommentsByParentAsync_ShouldReturnCommentsForParent()
        {
            // Arrange
            var parentFileId = Guid.NewGuid().ToString();
            var comment1 = TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: parentFileId);
            var comment2 = TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: parentFileId);
            var otherComment = TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: Guid.NewGuid().ToString());
            await _fileStorage.StoreFileMetadataAsync(comment1);
            await _fileStorage.StoreFileMetadataAsync(comment2);
            await _fileStorage.StoreFileMetadataAsync(otherComment);

            // Act
            var result = await _fileStorage.ListCommentsByParentAsync(parentFileId);

            // Assert
            result.Should().HaveCount(2);
            result.All(c => c.ParentFileId == parentFileId).Should().BeTrue();
        }

        [Fact]
        public async Task DeleteFileByFileIdAsync_ShouldDeleteFileMetadata()
        {
            // Arrange
            var metadata = TestDataBuilder.CreateFileMetadata();
            await _fileStorage.StoreFileMetadataAsync(metadata);

            // Act
            var result = await _fileStorage.DeleteFileByFileIdAsync(metadata.FileId);

            // Assert
            result.Should().BeTrue();
            var exists = await _fileStorage.FileExistsAsync(metadata.FileId);
            exists.Should().BeFalse();
        }

        [Fact]
        public async Task DeleteFileByFileIdAsync_NonExistentFile_ShouldReturnFalse()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();

            // Act
            var result = await _fileStorage.DeleteFileByFileIdAsync(fileId);

            // Assert
            result.Should().BeFalse();
        }

        public void Dispose()
        {
            _fileHelper?.Dispose();
        }
    }
}








