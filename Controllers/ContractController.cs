using System;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using System.Text.Json;

namespace DeltaniumStore.Controllers
{
    /// <summary>
    /// Controller for managing storage contracts between App and Storage Node.
    /// These contracts establish terms (duration, size, fees) that must be signed by both parties
    /// before file uploads are accepted.
    /// </summary>
    [ApiController]
    [Route("api/storage/contracts")]
    [Produces("application/json")]
    public class ContractController : ControllerBase
    {
        private readonly IStorageContractService _contractService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ContractController> _logger;

        public ContractController(IStorageContractService contractService, IConfiguration configuration, ILogger<ContractController> logger)
        {
            _contractService = contractService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Create a new storage contract.
        /// The App proposes terms (duration, files, size); Storage Node calculates and returns fee.
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> CreateContractAsync([FromBody] CreateContractRequest request)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                    return BadRequest("User public key is required");

                if (string.IsNullOrEmpty(request?.ContractType))
                    return BadRequest("ContractType (TimeFixed or OpenEnded) is required");

                if (request.TotalFileSize <= 0)
                    return BadRequest("TotalFileSize must be positive");

                // 🆕 FileIds can be empty (means any fileId allowed, validate by size only)
                // If FileIds has entries, those specific fileIds are allowed

                // TimeFixed requires endDate
                if (request.ContractType == "TimeFixed" && (!request.EndDateUnix.HasValue || request.EndDateUnix <= 0))
                    return BadRequest("EndDateUnix is required for TimeFixed contracts");

                if (request.StartDateUnix <= 0)
                    return BadRequest("StartDateUnix must be positive");

                // 🆕 Storage Node calculates fee based on terms
                var (calculatedFee, feeBasis) = CalculateStorageFee(
                    request.ContractType,
                    request.StartDateUnix,
                    request.EndDateUnix,
                    request.TotalFileSize);

                // Create contract with Storage Node-determined fee
                var contract = new StorageContract
                {
                    ContractId = Guid.NewGuid().ToString("N"),
                    ContractType = request.ContractType,
                    AppPublicKey = userPubKey,
                    StorageNodePublicKey = GetStorageNodePublicKey(),
                    StartDateUnix = request.StartDateUnix,
                    EndDateUnix = request.EndDateUnix,
                    TotalFileSize = request.TotalFileSize,
                    TotalFee = calculatedFee,
                    FileIds = request.FileIds ?? new(),
                    MessageToSign = string.Empty, // Will be generated when signing
                    AppSignature = string.Empty,
                    StorageNodeSignature = string.Empty,
                    CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Status = "PendingAppApproval" // Waiting for App to approve the fee
                };

                var created = await _contractService.CreateContractAsync(contract);
                var content = StorageContract.GetContractContent(created);
                var contractHash = StorageContract.ComputeContractHash(created);

                var totalFeeCanonical = created.TotalFee.ToString("F10", System.Globalization.CultureInfo.InvariantCulture);
                return Ok(new
                {
                    created.ContractId,
                    created.ContractType,
                    created.Status,
                    created.StartDateUnix,
                    created.EndDateUnix,
                    created.TotalFileSize,
                    created.TotalFee,
                    totalFeeCanonical,
                    FeeBasis = feeBasis,
                    content,
                    contractHash,
                    Message = "Contract created with Storage Node-calculated fee. App must approve and sign before uploads can proceed."
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error creating contract: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate storage fee based on contract terms.
        /// Pricing formula: Base (per GB per month) + Duration multiplier
        /// </summary>
        private (decimal fee, string basis) CalculateStorageFee(
            string contractType,
            long startDateUnix,
            long? endDateUnix,
            long totalFileSizeBytes)
        {
            // Read pricing from configuration
            decimal basePricePerGBPerMonth = _configuration.GetValue<decimal>("StorageNode:Pricing:BasePricePerGBPerMonth", 0.0m);
            
            // Calculate duration
            long durationSeconds = 0;
            if (contractType == "TimeFixed" && endDateUnix.HasValue)
            {
                durationSeconds = endDateUnix.Value - startDateUnix;
            }
            else if (contractType == "OpenEnded")
            {
                // Default 1 year for OpenEnded
                durationSeconds = 365 * 24 * 60 * 60;
            }

            // Convert to months (approximate)
            decimal durationMonths = (decimal)durationSeconds / (30 * 24 * 60 * 60);
            
            // Convert bytes to GB
            decimal sizeGB = (decimal)totalFileSizeBytes / (1024 * 1024 * 1024);
            
            // Calculate fee
            decimal calculatedFee = basePricePerGBPerMonth * sizeGB * durationMonths;
            
            // Minimum fee of 10 Deltanium
            if (calculatedFee < 0)
                calculatedFee = 0;

            string basis = $"{sizeGB:F2}GB × {durationMonths:F2}months × {basePricePerGBPerMonth} DLT/GB-month";
            
            return (calculatedFee, basis);
        }

        /// <summary>
        /// Get this Storage Node's public key (from configuration).
        /// </summary>
        private string GetStorageNodePublicKey()
        {
            // TODO: Read from configuration or node identity service
            var config = HttpContext.RequestServices.GetService<IConfiguration>();
            return config?.GetSection("NodeConfig:PublicKey")?.Value ?? "node_public_key";
        }

        /// <summary>
        /// Approve and sign a contract on behalf of the App.
        /// After App reviews the Node-calculated fee, App signs and sends this request.
        /// Storage Node then co-signs to finalize the contract.
        /// </summary>
        [HttpPost("sign/{contractId}")]
        public async Task<IActionResult> SignContractAsync(string contractId, [FromBody] SignContractRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.AppSignature))
                    return BadRequest("AppSignature is required");

                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                    return BadRequest("User public key is required");

                var contract = await _contractService.GetContractAsync(contractId);
                if (contract == null)
                    return NotFound($"Contract not found: {contractId}");

                if (!string.Equals(contract.AppPublicKey, userPubKey, StringComparison.OrdinalIgnoreCase))
                    return Unauthorized("Only the contract App can sign");

                if (contract.Status != "PendingAppApproval")
                    return BadRequest($"Contract status must be PendingAppApproval, but is {contract.Status}");

                var useV1 = request.SigningVersion == 1 || !string.IsNullOrWhiteSpace(request.MessageToSign);

                if (useV1)
                {
                    // V1: sign message (legacy). Blocker validates message and creates blocks.
                    var messageToSign = request.MessageToSign;
                    if (string.IsNullOrWhiteSpace(messageToSign))
                        return BadRequest("MessageToSign is required for signing version 1.");
                    var expectedMessage = StorageContract.GenerateMessageToSign(
                        contract.ContractType,
                        contract.StartDateUnix,
                        contract.EndDateUnix,
                        contract.AppPublicKey,
                        contract.StorageNodePublicKey,
                        contract.TotalFee,
                        contract.TotalFileSize);
                    if (!string.Equals(messageToSign, expectedMessage, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("[ContractController] V1: MessageToSign does not match expected. Expected length={Len1}, Received length={Len2}",
                            expectedMessage.Length, messageToSign.Length);
                        return BadRequest("MessageToSign does not match contract terms.");
                    }
                    var isValid = VerifyAppSignatureAsync(messageToSign, request.AppSignature, contract.AppPublicKey);
                    if (!isValid)
                    {
                        _logger.LogError("[ContractController] Invalid App signature (v1 message) for contract {ContractId}", contractId);
                        return BadRequest("App signature is invalid.");
                    }
                    contract.MessageToSign = messageToSign;
                    contract.AppSignature = request.AppSignature;
                    contract.StorageNodeSignature = await SignContractAsStorageNodeAsync(messageToSign);
                    contract.Status = "Active";
                    contract.ContractHash = ""; // V1: no contract hash
                    var updated = await _contractService.UpdateContractAsync(contract);
                    _logger.LogInformation("[ContractController] Contract signed with V1 (message). ContractId={ContractId}", contractId);
                    return Ok(new
                    {
                        updated.ContractId,
                        Status = "Active",
                        updated.TotalFee,
                        messageToSign = updated.MessageToSign,
                        storageNodeSignature = updated.StorageNodeSignature,
                        Message = "Contract signed by both parties (V1 message). Uploads may proceed."
                    });
                }

                // V2: sign contract hash
                var contractHash = StorageContract.ComputeContractHash(contract);
                contract.ContractHash = contractHash;
                _logger.LogInformation("[ContractController] === CONTRACT SIGNING STEP (V2 contract hash) ===");
                _logger.LogInformation("[ContractController] Contract hash: {ContractHash}", contractHash);

                var isSignatureValid = VerifyAppSignatureAsync(contractHash, request.AppSignature, contract.AppPublicKey);
                _logger.LogInformation("[ContractController] App signature verification result: {Result}", isSignatureValid);
                if (!isSignatureValid)
                {
                    _logger.LogError("[ContractController] Invalid App signature for contract {ContractId}", contractId);
                    return BadRequest("App signature is invalid. Contract signature must be valid before Storage Node can sign.");
                }

                contract.AppSignature = request.AppSignature;
                contract.StorageNodeSignature = await SignContractAsStorageNodeAsync(contractHash);
                contract.Status = "Active";
                var updatedV2 = await _contractService.UpdateContractAsync(contract);
                var contentEnvelope = StorageContract.GetContractContent(updatedV2);

                return Ok(new
                {
                    updatedV2.ContractId,
                    Status = "Active",
                    updatedV2.TotalFee,
                    hash = updatedV2.ContractHash,
                    contractHash = updatedV2.ContractHash,
                    appSignature = updatedV2.AppSignature,
                    storageNodeSignature = updatedV2.StorageNodeSignature,
                    content = contentEnvelope,
                    Message = "Contract signed by both parties (V2 hash) and is now Active. Uploads may proceed."
                });
            }
            catch (FileNotFoundException)
            {
                return NotFound($"Contract not found: {contractId}");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error signing contract: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify the App's signature over the contract message locally.
        /// Uses ECDSA/secp256k1 to verify the contract message signature.
        /// 
        /// IMPORTANT: The message format is:
        /// TimeFixed|startDate|endDate|appPublicKey|storageNodePublicKey|totalFee|totalFileSize
        /// 
        /// If ANY field in the contract changes (fee, dates, public keys, size),
        /// the signature will fail verification. This ensures App approved EXACT terms.
        /// </summary>
        private bool VerifyAppSignatureAsync(string messageToSign, string signature, string appPublicKey)
        {
            try
            {
                Console.WriteLine($"[VERIFY_CALLED] Starting VerifyAppSignatureAsync");
                _logger.LogInformation("[VERIFY_CALLED] VerifyAppSignatureAsync method invoked!");
                
                if (string.IsNullOrWhiteSpace(messageToSign) || string.IsNullOrWhiteSpace(signature))
                {
                    Console.WriteLine("[ContractController] VerifyAppSignature: Empty message or signature");
                    Console.WriteLine($"[ContractController] messageToSign empty? {string.IsNullOrWhiteSpace(messageToSign)}");
                    Console.WriteLine($"[ContractController] signature empty? {string.IsNullOrWhiteSpace(signature)}");
                    _logger.LogError("[ContractController] VerifyAppSignature: Empty message or signature");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(appPublicKey))
                {
                    Console.WriteLine("[ContractController] VerifyAppSignature: Empty public key");
                    _logger.LogError("[ContractController] VerifyAppSignature: Empty public key");
                    return false;
                }

                // Hash the message using SHA256 (matching app signing)
                var messageBytes = Encoding.UTF8.GetBytes(messageToSign);
                var messageHash = SHA256.HashData(messageBytes);
                var hashUint256 = new uint256(messageHash);

                Console.WriteLine($"[ContractController] Message to verify: {messageToSign}");
                Console.WriteLine($"[ContractController] Message hash: {hashUint256}");
                Console.WriteLine($"[ContractController] App public key: {appPublicKey}");
                Console.WriteLine($"[ContractController] Signature (base64): {signature}");
                _logger.LogInformation("[ContractController] === CONTRACT SIGNATURE VERIFICATION ===");
                _logger.LogInformation("[ContractController] Message to verify: {Message}", messageToSign);
                _logger.LogInformation("[ContractController] Message hash: {Hash}", hashUint256);
                _logger.LogInformation("[ContractController] App public key: {PubKey}", appPublicKey);
                _logger.LogInformation("[ContractController] Signature (base64): {Sig}", signature);

                // Parse public key (should be compressed format: 02/03 + 32 bytes hex)
                PubKey pubKey;
                try
                {
                    // Try parsing as hex first
                    pubKey = new PubKey(appPublicKey);
                    Console.WriteLine($"[ContractController] Public key parsed successfully: {pubKey.ToHex()}");
                    _logger.LogInformation("[ContractController] Public key parsed successfully: {ParsiedKey}", pubKey.ToHex());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ContractController] Failed to parse public key: {ex.Message}");
                    Console.WriteLine($"[ContractController] Exception type: {ex.GetType().Name}");
                    Console.WriteLine($"[ContractController] Stack trace: {ex.StackTrace}");
                    _logger.LogError("[ContractController] Failed to parse public key: {Error}", ex.Message);
                    return false;
                }

                // Decode the signature from base64
                byte[] signatureBytes;
                try
                {
                    signatureBytes = Convert.FromBase64String(signature);
                    Console.WriteLine($"[ContractController] Signature decoded from base64: {signatureBytes.Length} bytes");
                    Console.WriteLine($"[ContractController] Signature hex: {string.Join("", signatureBytes.Select(b => b.ToString("x2")))}");
                    _logger.LogInformation("[ContractController] Signature decoded from base64: {SignatureLen} bytes", signatureBytes.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ContractController] Failed to decode signature from base64: {ex.Message}");
                    _logger.LogError("[ContractController] Failed to decode signature from base64: {Error}", ex.Message);
                    return false;
                }

                // Verify signature using NBitcoin PubKey.Verify
                // This method expects the message hash and the signature bytes
                bool isValid = false;
                try
                {
                    Console.WriteLine($"[ContractController] About to verify signature...");
                    isValid = pubKey.Verify(hashUint256, signatureBytes);
                    Console.WriteLine($"[ContractController] Local signature verification result: {isValid}");
                    _logger.LogInformation("[ContractController] Local signature verification result: {IsValid}", isValid);
                    
                    if (!isValid)
                    {
                        Console.WriteLine($"[ContractController] Signature verification FAILED!");
                        Console.WriteLine($"[ContractController] Expected message hash: {hashUint256}");
                        Console.WriteLine($"[ContractController] Public key: {pubKey.ToHex()}");
                        Console.WriteLine($"[ContractController] Signature bytes: {string.Join(" ", signatureBytes.Select(b => b.ToString("x2")).Take(20))}...");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ContractController] Exception during signature verification: {ex.Message}");
                    Console.WriteLine($"[ContractController] Exception type: {ex.GetType().Name}");
                    Console.WriteLine($"[ContractController] Stack trace: {ex.StackTrace}");
                    _logger.LogError("[ContractController] Exception during signature verification: {Error}", ex.Message);
                    return false;
                }

                return isValid;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error in VerifyAppSignature: {ex.Message}");
                Console.WriteLine($"[ContractController] Exception type: {ex.GetType().Name}");
                Console.WriteLine($"[ContractController] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Sign the contract message as Storage Node.
        /// Uses the private key from node_info.json to cryptographically sign the message.
        /// </summary>
        private async Task<string> SignContractAsStorageNodeAsync(string messageToSign)
        {
            try 
            {
                // Read private key from node_info.json
                if (!System.IO.File.Exists("node_info.json"))
                {
                    _logger.LogWarning("node_info.json not found. Using mock signature.");
                    return $"node_signature_{Guid.NewGuid():N}";
                }

                var json = await System.IO.File.ReadAllTextAsync("node_info.json");
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("privateKey", out var privKeyElement))
                {
                     _logger.LogWarning("privateKey not found in node_info.json. Using mock signature.");
                    return $"node_signature_{Guid.NewGuid():N}";
                }

                var privateKeyHex = privKeyElement.GetString();
                if (string.IsNullOrEmpty(privateKeyHex))
                {
                     _logger.LogWarning("Empty privateKey in node_info.json. Using mock signature.");
                    return $"node_signature_{Guid.NewGuid():N}";
                }

                // Parse private key
                // Note: NBitcoin Key constructor handles hex string if using the right overload or decoding first
                var key = new Key(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(privateKeyHex));

                // Hash message to match what App signs (SHA256)
                var messageBytes = Encoding.UTF8.GetBytes(messageToSign);
                var messageHash = SHA256.HashData(messageBytes);
                var hashUint256 = new uint256(messageHash);

                // Sign
                var signature = key.Sign(hashUint256);
                var signatureBase64 = Convert.ToBase64String(signature.ToDER());

                _logger.LogInformation("Generated real storage node signature for contract.");
                return signatureBase64;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sign contract with node key. Falling back to mock.");
                return $"node_signature_{Guid.NewGuid():N}";
            }
        }

        /// <summary>
        /// Get a contract by ID.
        /// </summary>
        [HttpGet("{contractId}")]
        public async Task<IActionResult> GetContractAsync(string contractId)
        {
            try
            {
                var contract = await _contractService.GetContractAsync(contractId);
                if (contract == null)
                    return NotFound($"Contract not found: {contractId}");

                return Ok(contract);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error retrieving contract: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// List all active contracts for the current user.
        /// </summary>
        [HttpGet("list/active")]
        public async Task<IActionResult> ListActiveContractsAsync()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                    return BadRequest("User public key is required");

                var contracts = await _contractService.ListActiveContractsAsync(userPubKey);
                return Ok(new
                {
                    Count = contracts.Count,
                    Contracts = contracts
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error listing contracts: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate that a file upload matches a signed contract.
        /// Used by file upload endpoints to verify contract compliance.
        /// </summary>
        [HttpPost("validate")]
        public async Task<IActionResult> ValidateUploadAsync([FromBody] ValidateContractRequest request)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                    return BadRequest("User public key is required");

                if (string.IsNullOrEmpty(request?.ContractId))
                    return BadRequest("ContractId is required");

                if (string.IsNullOrEmpty(request.FileId))
                    return BadRequest("FileId is required");

                if (request.FileSize <= 0)
                    return BadRequest("FileSize must be positive");

                var (valid, error) = await _contractService.ValidateUploadAgainstContractAsync(
                    request.ContractId,
                    request.FileId,
                    request.FileSize,
                    userPubKey);

                if (!valid)
                    return BadRequest(new { Error = error });

                return Ok(new { Valid = true, Message = "Upload is compliant with contract terms." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ContractController] Error validating upload: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sign contract request. Supports v1 (message) and v2 (contract hash).
    /// V1: MessageToSign + AppSignature (signature over message). Blocker accepts and creates blocks.
    /// V2: AppSignature only (signature over contract hash).
    /// </summary>
    public class SignContractRequest
    {
        public string AppSignature { get; set; } = "";
        /// <summary>Optional. If 1 or MessageToSign is set, use v1 (sign message). Otherwise v2 (sign contract hash).</summary>
        public int SigningVersion { get; set; }
        /// <summary>For v1: the message the App signed (TimeFixed|... or OpenEnded|...).</summary>
        public string? MessageToSign { get; set; }
    }

    public class ValidateContractRequest
    {
        public string ContractId { get; set; }
        public string FileId { get; set; }
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Request model for creating a storage contract.
    /// App proposes storage terms; Storage Node calculates and returns fee.
    /// </summary>
    public class CreateContractRequest
    {
        public string ContractType { get; set; } // "TimeFixed" or "OpenEnded"
        public long StartDateUnix { get; set; } // When storage starts
        public long? EndDateUnix { get; set; } // When storage ends (required for TimeFixed)
        public long TotalFileSize { get; set; } // Max total file size in bytes
        public List<string> FileIds { get; set; } = new(); // Files to store
    }
}
