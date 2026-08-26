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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace deltanium_store.Tests.Controllers
{
    public class PostControllerTests
    {
        private readonly Mock<IFileStorageService> _mockFileStorage;
        private readonly Mock<ICentralApiService> _mockCentralApi;
        private readonly PostController _controller;

        public PostControllerTests()
        {
            _mockFileStorage = new Mock<IFileStorageService>();
            _mockCentralApi = new Mock<ICentralApiService>();
            _controller = new PostController(_mockFileStorage.Object, _mockCentralApi.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        [Fact]
        public async Task GetReactions_ShouldReturnCountsAndMyReaction()
        {
            // Arrange
            var postId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var reactions = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: postId, kind: "like", ownerPubKey: userKey),
                TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: postId, kind: "love", ownerPubKey: TestDataBuilder.CreateCompressedPublicKey()),
                TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: postId, kind: "love", ownerPubKey: TestDataBuilder.CreateCompressedPublicKey())
            };
            _mockFileStorage.Setup(s => s.ListReactionsByParentAsync(postId)).ReturnsAsync(reactions);

            // Act
            var result = await _controller.GetReactions(postId) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("Total").GetInt32().Should().Be(3);
            payload.GetProperty("ByKind").GetProperty("like").GetInt32().Should().Be(1);
            payload.GetProperty("ByKind").GetProperty("love").GetInt32().Should().Be(2);
            payload.GetProperty("MyReaction").GetString().Should().Be("like");
        }

        [Fact]
        public async Task RemoveReaction_ShouldDeleteUserReactions()
        {
            // Arrange
            var postId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var reactions = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: postId, kind: "like", ownerPubKey: userKey),
                TestDataBuilder.CreateFileMetadata(type: "react", parentFileId: postId, kind: "love", ownerPubKey: TestDataBuilder.CreateCompressedPublicKey())
            };
            _mockFileStorage.Setup(s => s.ListReactionsByParentAsync(postId)).ReturnsAsync(reactions);
            _mockFileStorage.Setup(s => s.DeleteFileByFileIdAsync(It.IsAny<string>())).ReturnsAsync(true);

            // Act
            var result = await _controller.RemoveReaction(postId, kind: "like") as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("Removed").GetInt32().Should().Be(1);
        }

        [Fact]
        public async Task GetComments_ShouldFilterAccessible()
        {
            // Arrange
            var postId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var comments = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: postId, isPublic: true), // accessible
                TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: postId, recipientPubKey: userKey), // accessible via recipient
                TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: postId, ownerPubKey: userKey), // accessible via owner
                TestDataBuilder.CreateFileMetadata(type: "comment", parentFileId: postId, recipientPubKey: TestDataBuilder.CreateCompressedPublicKey()) // not accessible
            };
            _mockFileStorage.Setup(s => s.ListCommentsByParentAsync(postId)).ReturnsAsync(comments);

            // Act
            var result = await _controller.GetComments(postId) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("Count").GetInt32().Should().Be(3); // excludes inaccessible one
        }

        [Fact]
        public async Task DeleteComment_WhenOwner_ShouldDelete()
        {
            // Arrange
            var commentId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var comment = TestDataBuilder.CreateFileMetadata(type: "comment", ownerPubKey: userKey);
            _mockFileStorage.Setup(s => s.GetFileMetadataAsync(commentId, userKey)).ReturnsAsync(comment);
            _mockFileStorage.Setup(s => s.DeleteFileByFileIdAsync(commentId)).ReturnsAsync(true);

            // Act
            var result = await _controller.DeleteComment(commentId) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("Deleted").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task DeleteComment_NotOwner_ShouldForbid()
        {
            // Arrange
            var commentId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var comment = TestDataBuilder.CreateFileMetadata(type: "comment", ownerPubKey: TestDataBuilder.CreateCompressedPublicKey());
            _mockFileStorage.Setup(s => s.GetFileMetadataAsync(commentId, userKey)).ReturnsAsync(comment);

            // Act
            var result = await _controller.DeleteComment(commentId);

            // Assert
            result.Should().BeOfType<ForbidResult>();
        }

        [Fact]
        public async Task DeleteComment_NotFound_ShouldReturnNotFound()
        {
            // Arrange
            var commentId = Guid.NewGuid().ToString();
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            _mockFileStorage.Setup(s => s.GetFileMetadataAsync(commentId, userKey))
                .ReturnsAsync((FileMetadata?)null);

            // Act
            var result = await _controller.DeleteComment(commentId);

            // Assert
            result.Should().BeOfType<NotFoundObjectResult>();
        }

        [Fact]
        public async Task GetUserPosts_ShouldReturnPosts()
        {
            // Arrange
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            var posts = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey, type: "post"),
                TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey, type: "post")
            };
            _mockFileStorage.Setup(s => s.ListUserFilesAsync(userKey)).ReturnsAsync(posts);

            // Act
            var result = await _controller.GetUserPosts(userKey) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("PostCount").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task GetTimeline_ShouldReturnOwnPosts()
        {
            // Arrange
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            var posts = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(recipientPubKey: userKey, type: "post")
            };
            _mockFileStorage.Setup(s => s.ListUserFilesAsync(userKey)).ReturnsAsync(posts);

            // Act
            var result = await _controller.GetTimeline() as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("PostCount").GetInt32().Should().Be(1);
        }

        [Fact]
        public async Task GetFollowersFeed_ShouldReturnUnionOptions()
        {
            // Arrange
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;

            var followerPosts = new List<FileMetadata>
            {
                // Option 1: per recipient
                TestDataBuilder.CreateFileMetadata(type: "post", shareType: "followers", recipientPubKey: userKey),
                // Option 2: tag-based
                TestDataBuilder.CreateFileMetadata(type: "post", shareType: "followers", recipientPubKey: string.Empty, policyTag: "followers:2025", capsuleFor: "tag", encryptedType: "encrypted")
            };
            _mockFileStorage.Setup(s => s.ListAllPostsByShareTypeAsync("followers")).ReturnsAsync(followerPosts);

            // Act
            var result = await _controller.GetFollowersFeed(limit: 10) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("PostCount").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task GetPublicFeed_ShouldReturnPublicPosts()
        {
            // Arrange
            var publicPosts = new List<FileMetadata>
            {
                TestDataBuilder.CreateFileMetadata(type: "post", isPublic: true),
                TestDataBuilder.CreateFileMetadata(type: "post", encryptedType: "public")
            };
            _mockFileStorage.Setup(s => s.ListAllPublicPostsAsync()).ReturnsAsync(publicPosts);

            // Act
            var result = await _controller.GetPublicFeed(limit: 10) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("PostCount").GetInt32().Should().Be(2);
        }

        [Fact]
        public async Task GetFollowingFeed_NoFollowingUsers_ShouldReturnEmpty()
        {
            // Arrange
            var userKey = TestDataBuilder.CreateCompressedPublicKey();
            _controller.HttpContext.Request.Headers["X-User-PubKey"] = userKey;
            _mockCentralApi.Setup(c => c.GetFollowingUsersAsync(userKey)).ReturnsAsync(new List<string>());

            // Act
            var result = await _controller.GetFollowingFeed(limit: 5) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            var payload = JsonSerializer.SerializeToElement(result!.Value);
            payload.GetProperty("PostCount").GetInt32().Should().Be(0);
        }
    }
}

