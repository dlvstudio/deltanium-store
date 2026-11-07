using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace deltanium_store.Tests.Services;

public class BlockchainServiceTests
{
    private static (BlockchainService Service, CapturingHandler Handler) CreateServiceWithCapturingHandler(HttpStatusCode responseStatus = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(responseStatus);
        var client = new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:5003") };
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Blockchain:BlockerApi"]).Returns("http://localhost:5003");
        config.Setup(c => c["Store:NodePublicKey"]).Returns("03699b56070f41068ca1166169547a4bd05022c41e184069352163dfe21553a14c");
        var logger = new Mock<ILogger<BlockchainService>>();
        var service = new BlockchainService(client, config.Object, logger.Object);
        return (service, handler);
    }

    [Fact]
    public async Task SubmitStorageOperationAsync_SendsEnvelope_WithHashAppSignatureStorageNodeSignatureContent()
    {
        var (service, handler) = CreateServiceWithCapturingHandler();
        var contract = TestDataBuilder.CreateStorageContract(
            appPublicKey: "appPK",
            storageNodePublicKey: "nodePK");
        contract.ContractHash = "abc123hash";
        contract.AppSignature = "appSig";
        contract.StorageNodeSignature = "nodeSig";
        var metadata = TestDataBuilder.CreateFileMetadata();

        await service.SubmitStorageOperationAsync(metadata, contract);

        handler.LastRequestContent.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestContent!);
        body.TryGetProperty("type", out var type).Should().BeTrue();
        type.GetString().Should().Be("StorageOperation");
        body.TryGetProperty("data", out var data).Should().BeTrue();
        data.TryGetProperty("contract", out var contractEl).Should().BeTrue();
        contractEl.TryGetProperty("hash", out var hash).Should().BeTrue();
        hash.GetString().Should().Be("abc123hash");
        contractEl.TryGetProperty("appSignature", out var appSig).Should().BeTrue();
        appSig.GetString().Should().Be("appSig");
        contractEl.TryGetProperty("storageNodeSignature", out var nodeSig).Should().BeTrue();
        nodeSig.GetString().Should().Be("nodeSig");
        contractEl.TryGetProperty("content", out var content).Should().BeTrue();
        content.ValueKind.Should().Be(JsonValueKind.Object);
        content.TryGetProperty("appPublicKey", out _).Should().BeTrue();
        content.TryGetProperty("storageNodePublicKey", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitStorageOperationAsync_SendsFeeAsF10String()
    {
        var (service, handler) = CreateServiceWithCapturingHandler();
        var contract = TestDataBuilder.CreateStorageContract(totalFee: 0.0000001000m);
        contract.ContractHash = "h";
        contract.AppSignature = "a";
        contract.StorageNodeSignature = "s";
        var metadata = TestDataBuilder.CreateFileMetadata();

        await service.SubmitStorageOperationAsync(metadata, contract);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestContent!);
        body.TryGetProperty("fee", out var fee).Should().BeTrue();
        var feeStr = fee.ValueKind == JsonValueKind.String ? fee.GetString() : fee.GetRawText();
        feeStr.Should().Be("0.0000001000");
    }

    [Fact]
    public async Task SubmitStorageOperationAsync_FromToMatchContractParties()
    {
        var (service, handler) = CreateServiceWithCapturingHandler();
        var contract = TestDataBuilder.CreateStorageContract(
            appPublicKey: "fromKey",
            storageNodePublicKey: "toKey");
        contract.ContractHash = "h";
        contract.AppSignature = "a";
        contract.StorageNodeSignature = "s";
        var metadata = TestDataBuilder.CreateFileMetadata();

        await service.SubmitStorageOperationAsync(metadata, contract);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestContent!);
        body.TryGetProperty("from", out var from).Should().BeTrue();
        from.GetString().Should().Be("fromKey");
        body.TryGetProperty("to", out var to).Should().BeTrue();
        to.GetString().Should().Be("toKey");
    }

    [Fact]
    public async Task SubmitStorageOperationAsync_OnSuccess_ReturnsSuccess()
    {
        var (service, handler) = CreateServiceWithCapturingHandler(HttpStatusCode.OK);
        var contract = TestDataBuilder.CreateStorageContract();
        contract.ContractHash = "h";
        contract.AppSignature = "a";
        contract.StorageNodeSignature = "s";
        var metadata = TestDataBuilder.CreateFileMetadata();

        var result = await service.SubmitStorageOperationAsync(metadata, contract);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SubmitStorageOperationAsync_On503_ReturnsFailure()
    {
        var (service, handler) = CreateServiceWithCapturingHandler(HttpStatusCode.ServiceUnavailable);
        var contract = TestDataBuilder.CreateStorageContract();
        contract.ContractHash = "h";
        contract.AppSignature = "a";
        contract.StorageNodeSignature = "s";
        var metadata = TestDataBuilder.CreateFileMetadata();

        var result = await service.SubmitStorageOperationAsync(metadata, contract);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _responseStatus;
        public string? LastRequestContent { get; private set; }

        public CapturingHandler(HttpStatusCode responseStatus = HttpStatusCode.OK)
        {
            _responseStatus = responseStatus;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestContent = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(_responseStatus));
        }
    }
}
