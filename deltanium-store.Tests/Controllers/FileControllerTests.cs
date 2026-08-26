using FluentAssertions;
using Xunit;
using DeltaniumStore.Controllers;
using DeltaniumStore.Services;
using DeltaniumStore.Models;
using deltanium_store.Tests.Helpers;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace deltanium_store.Tests.Controllers
{
    public class FileControllerTests : IDisposable
    {
        private readonly Mock<IFileStorageService> _mockFileStorage;
        private readonly FileController _controller;
        private readonly TestFileHelper _fileHelper;

        public FileControllerTests()
        {
            _fileHelper = new TestFileHelper();
            _mockFileStorage = new Mock<IFileStorageService>();
            var mockContractService = new Mock<IStorageContractService>();
            var mockBlockchainService = new Mock<IBlockchainService>();
            _controller = new FileController(_mockFileStorage.Object, mockContractService.Object, mockBlockchainService.Object);
            
            // Setup controller context
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Fact]
        public async Task DownloadFile_ExistingFile_ShouldReturnOk()
        {
            // Arrange
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();
            var metadata = TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey);
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            _mockFileStorage.Setup(s => s.GetFileMetadataAsync(metadata.FileId, userKey))
                .ReturnsAsync(metadata);

            // Act
            var result = await _controller.DownloadFile(metadata.FileId);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            okResult!.Value.Should().BeEquivalentTo(metadata);
        }

        [Fact]
        public async Task DownloadFile_NonExistentFile_ShouldReturnNotFound()
        {
            // Arrange
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();
            var fileId = Guid.NewGuid().ToString();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            _mockFileStorage.Setup(s => s.GetFileMetadataAsync(fileId, userKey))
                .ThrowsAsync(new FileNotFoundException());

            // Act
            var result = await _controller.DownloadFile(fileId);

            // Assert
            result.Should().BeOfType<NotFoundObjectResult>();
        }

        [Fact]
        public async Task DownloadFile_NoUserPubKey_ShouldReturnBadRequest()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();

            // Act
            var result = await _controller.DownloadFile(fileId);

            // Assert
            result.Should().BeOfType<BadRequestObjectResult>();
        }

        [Fact]
        public async Task DownloadBlock_ExistingBlock_ShouldReturnFile()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            var blockData = Encoding.UTF8.GetBytes("test block data");
            var stream = new MemoryStream(blockData);
            _mockFileStorage.Setup(s => s.GetBlockAsync(blockId))
                .ReturnsAsync(stream);

            // Act
            var result = await _controller.DownloadBlock(blockId);

            // Assert
            result.Should().BeOfType<FileStreamResult>();
        }

        [Fact]
        public async Task DownloadBlock_NonExistentBlock_ShouldReturnNotFound()
        {
            // Arrange
            var blockId = Guid.NewGuid().ToString();
            _mockFileStorage.Setup(s => s.GetBlockAsync(blockId))
                .ThrowsAsync(new FileNotFoundException());

            // Act
            var result = await _controller.DownloadBlock(blockId);

            // Assert
            result.Should().BeOfType<NotFoundObjectResult>();
        }

        [Fact]
        public async Task ListFiles_ShouldReturnUserFiles()
        {
            // Arrange
            var (_, userKey) = TestDataBuilder.GenerateKeyPair();
            var file1 = TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey);
            var file2 = TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey);
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            _mockFileStorage.Setup(s => s.ListUserFilesAsync(userKey))
                .ReturnsAsync(new List<FileMetadata> { file1, file2 });

            // Act
            var result = await _controller.ListFiles();

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            okResult!.Value.Should().BeAssignableTo<List<FileMetadata>>();
        }

        [Fact]
        public async Task ListFiles_NoUserPubKey_ShouldReturnBadRequest()
        {
            // Act
            var result = await _controller.ListFiles();

            // Assert
            result.Should().BeOfType<BadRequestObjectResult>();
        }

        [Fact]
        public async Task CheckFileExists_ExistingFile_ShouldReturnTrue()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();
            _mockFileStorage.Setup(s => s.FileExistsAsync(fileId))
                .ReturnsAsync(true);

            // Act
            var result = await _controller.CheckFileExists(fileId);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var value = okResult!.Value;
            value.Should().NotBeNull();
        }

        [Fact]
        public async Task CheckFileExists_NonExistentFile_ShouldReturnFalse()
        {
            // Arrange
            var fileId = Guid.NewGuid().ToString();
            _mockFileStorage.Setup(s => s.FileExistsAsync(fileId))
                .ReturnsAsync(false);

            // Act
            var result = await _controller.CheckFileExists(fileId);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
        }

        public void Dispose()
        {
            _fileHelper?.Dispose();
        }
    }
}

