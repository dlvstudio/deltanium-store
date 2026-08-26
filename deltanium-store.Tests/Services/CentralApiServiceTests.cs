using FluentAssertions;
using Xunit;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;
using Moq;
using Moq.Protected;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace deltanium_store.Tests.Services
{
    public class CentralApiServiceTests
    {
        private readonly Mock<HttpMessageHandler> _mockHttpHandler;
        private readonly HttpClient _httpClient;
        private readonly Mock<Microsoft.Extensions.Configuration.IConfiguration> _mockConfig;
        private readonly Mock<Microsoft.Extensions.Configuration.IConfigurationSection> _mockConfigSection;
        private readonly CentralApiService _service;

        public CentralApiServiceTests()
        {
            _mockHttpHandler = new Mock<HttpMessageHandler>();
            _httpClient = new HttpClient(_mockHttpHandler.Object);
            _mockConfig = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
            _mockConfigSection = new Mock<Microsoft.Extensions.Configuration.IConfigurationSection>();
            
            // Setup default configuration
            _mockConfig.Setup(c => c["CentralApi:BaseUrl"]).Returns("http://localhost:5002");
            _mockConfigSection.Setup(c => c.Value).Returns("30");
            _mockConfig.Setup(c => c.GetSection("CentralApi:Timeout")).Returns(_mockConfigSection.Object);
            
            _service = new CentralApiService(_httpClient, _mockConfig.Object);
        }

        [Fact]
        public async Task GetFollowingUsersAsync_ShouldReturnListOfFollowingUsers()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();
            var followingUsers = new[]
            {
                new { publicKey = TestDataBuilder.CreateCompressedPublicKey() },
                new { publicKey = TestDataBuilder.CreateCompressedPublicKey() }
            };
            
            var responseJson = JsonSerializer.Serialize(new { users = followingUsers });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => 
                        req.RequestUri.ToString().Contains("/api/user/following")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.GetFollowingUsersAsync(userPubKey);

            // Assert
            result.Should().HaveCount(2);
            result.Should().Contain(followingUsers[0].publicKey);
            result.Should().Contain(followingUsers[1].publicKey);
        }

        [Fact]
        public async Task GetFollowingUsersAsync_EmptyResponse_ShouldReturnEmptyList()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.GetFollowingUsersAsync(userPubKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetFollowingUsersAsync_ApiError_ShouldReturnEmptyList()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.GetFollowingUsersAsync(userPubKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetFollowingUsersAsync_Exception_ShouldReturnEmptyList()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Network error"));

            // Act
            var result = await _service.GetFollowingUsersAsync(userPubKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetUserInfoAsync_ShouldReturnUserInfoDictionary()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();
            var userInfo = new Dictionary<string, object>
            {
                { "publicKey", userPubKey },
                { "fullName", "Test User" },
                { "email", "test@example.com" }
            };
            
            var responseJson = JsonSerializer.Serialize(userInfo);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => 
                        req.RequestUri.ToString().Contains("/api/user/")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.GetUserInfoAsync(userPubKey);

            // Assert
            result.Should().NotBeEmpty();
            result.Should().ContainKey("publicKey");
            result.Should().ContainKey("fullName");
        }

        [Fact]
        public async Task GetUserInfoAsync_ApiError_ShouldReturnEmptyDictionary()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.GetUserInfoAsync(userPubKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetUserInfoAsync_Exception_ShouldReturnEmptyDictionary()
        {
            // Arrange
            var userPubKey = TestDataBuilder.CreateCompressedPublicKey();

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Network error"));

            // Act
            var result = await _service.GetUserInfoAsync(userPubKey);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task ValidatePreTokenAsync_ValidToken_ShouldReturnRkBase64()
        {
            // Arrange
            var token = "valid_token_123";
            var rkBase64 = "dGVzdF9yZWtleV9kYXRh";
            var responseJson = JsonSerializer.Serialize(new { rk = rkBase64 });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => 
                        req.RequestUri.ToString().Contains("/api/policy/validate-token")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.ValidatePreTokenAsync(token);

            // Assert
            result.Should().Be(rkBase64);
        }

        [Fact]
        public async Task ValidatePreTokenAsync_InvalidToken_ShouldReturnNull()
        {
            // Arrange
            var token = "invalid_token";
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.ValidatePreTokenAsync(token);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task ValidatePreTokenAsync_NoRkInResponse_ShouldReturnNull()
        {
            // Arrange
            var token = "valid_token_123";
            var responseJson = JsonSerializer.Serialize(new { message = "Token valid but no rk" });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

            // Act
            var result = await _service.ValidatePreTokenAsync(token);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task ValidatePreTokenAsync_Exception_ShouldReturnNull()
        {
            // Arrange
            var token = "test_token";

            _mockHttpHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Network error"));

            // Act
            var result = await _service.ValidatePreTokenAsync(token);

            // Assert
            result.Should().BeNull();
        }
    }
}

