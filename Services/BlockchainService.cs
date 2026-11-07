using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using DeltaniumStore.Models;

using Microsoft.Extensions.Logging;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Service for submitting transactions to the blockchain.
    /// Store submits a single StorageOperation transaction that includes the signed contract.
    /// </summary>
    public interface IBlockchainService
    {
        /// <summary>
        /// Submit a storage operation transaction with contract proof.
        /// </summary>
        Task<SubmitResult> SubmitStorageOperationAsync(FileMetadata metadata, StorageContract contract);
    }

    public record SubmitResult(bool Success, string? ErrorMessage = null, HttpStatusCode? StatusCode = null);

    public class BlockchainService : IBlockchainService
    {
        private readonly HttpClient _httpClient;
        private readonly string _blockerApiUrl;
        private readonly string _storeNodePublicKey;
        private readonly ILogger<BlockchainService> _logger;
        private readonly Random _jitter = new();

        public BlockchainService(HttpClient httpClient, IConfiguration configuration, ILogger<BlockchainService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _blockerApiUrl = configuration["Blockchain:BlockerApi"] ?? "http://localhost:5003";
            _storeNodePublicKey = configuration["Store:NodePublicKey"] ?? "03699b56070f41068ca1166169547a4bd05022c41e184069352163dfe21553a14c";
            
            _logger.LogInformation($"[BlockchainService] Initialized with BlockerApi: {_blockerApiUrl}");
        }

        /// <summary>
        /// Submit a StorageOperation transaction that includes contract proof.
        /// The contract itself proves the App agreed to pay the storage fee.
        /// </summary>
        public async Task<SubmitResult> SubmitStorageOperationAsync(FileMetadata metadata, StorageContract contract)
        {
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var txId = GenerateTransactionId();

                var txBody = new
                {
                    id = txId,
                    type = "StorageOperation",
                    from = contract.AppPublicKey,
                    to = contract.StorageNodePublicKey,
                    data = new
                    {
                        contractId = contract.ContractId,
                        fileId = metadata.FileId,
                        shareType = metadata.ShareType,
                        encryptedType = metadata.EncryptedType,
                        storageNodePubKey = _storeNodePublicKey,
                        contract = new
                        {
                            hash = contract.ContractHash,
                            appSignature = contract.AppSignature,
                            storageNodeSignature = contract.StorageNodeSignature,
                            content = StorageContract.GetContractContent(contract)
                        }
                    },
                    timestamp = timestamp,
                    fee = contract.TotalFee.ToString("F10", System.Globalization.CultureInfo.InvariantCulture) as string, // Use actual fee from contract
                    signature = contract.AppSignature,
                    signPayload = !string.IsNullOrEmpty(contract.ContractHash) ? contract.ContractHash : contract.MessageToSign
                };

                _logger.LogInformation($"[BlockchainService] Submitting StorageOperation tx: {txId}");
                
                // Compact JSON so Blocker's canonical rebuild from content matches (no whitespace difference)
                var options = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString
                };
                var json = JsonSerializer.Serialize(txBody, options);
                _logger.LogInformation("[BlockchainService] Submitting tx payload (length={Length})", json.Length);
                var requestUrl = $"{_blockerApiUrl}/api/tx/submit";
                var maxAttempts = 3;
                var delayMs = 500;

                string? lastError = null;
                HttpStatusCode? lastStatus = null;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        var resp = await _httpClient.PostAsync(requestUrl, content);
                        if (resp.IsSuccessStatusCode)
                        {
                            _logger.LogInformation($"[BlockchainService] StorageOperation submitted: {txId}");
                            return new SubmitResult(true);
                        }

                        var errorBody = await resp.Content.ReadAsStringAsync();
                        _logger.LogError($"[BlockchainService] Submit failed ({resp.StatusCode}): {errorBody}");
                        lastError = errorBody;
                        lastStatus = resp.StatusCode;

                        if (!ShouldRetry(resp.StatusCode) || attempt == maxAttempts)
                        {
                            return new SubmitResult(false, lastError, lastStatus);
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError($"[BlockchainService] Submit attempt {attempt} failed: {ex.Message}");
                        lastError = ex.Message;
                        if (attempt == maxAttempts)
                        {
                            return new SubmitResult(false, lastError);
                        }
                    }

                    var jitterMs = _jitter.Next(0, 200);
                    await Task.Delay(delayMs + jitterMs);
                    delayMs *= 2;
                }

                return new SubmitResult(false, lastError, lastStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[BlockchainService] Error submitting transaction: {ex.Message}");
                return new SubmitResult(false, ex.Message);
            }
        }

        private string GenerateTransactionId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 32);
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 408 || code == 429 || code >= 500;
        }

    }
}
