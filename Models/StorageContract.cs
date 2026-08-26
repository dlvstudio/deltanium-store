using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace DeltaniumStore.Models
{
    /// <summary>
    /// Storage Contract model for Deltanium Store.
    /// Represents a signed agreement between an App (user) and Storage Node for file storage.
    /// Both parties sign the contract hash (SHA-256 of canonical JSON); MessageToSign is deprecated.
    /// </summary>
    public class StorageContract
    {
        public string ContractId { get; set; } = string.Empty;
        public string ContractType { get; set; } = string.Empty; // "TimeFixed" or "OpenEnded"

        // Parties involved
        public string AppPublicKey { get; set; } = string.Empty;
        public string StorageNodePublicKey { get; set; } = string.Empty;

        // Contract terms
        public long StartDateUnix { get; set; }
        public long? EndDateUnix { get; set; } // null for OpenEnded
        public long TotalFileSize { get; set; } // bytes
        public decimal TotalFee { get; set; } // Deltanium
        /// <summary>F10 fee string for canonical hash; included in API responses so App (Dart) can match C# hash.</summary>
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("totalFeeCanonical")]
        public string TotalFeeCanonical => TotalFee.ToString("F10", CultureInfo.InvariantCulture);
        public List<string> FileIds { get; set; } = new();

        // Signatures and proof
        /// <summary>Hash of canonical contract (hex lowercase). This is what both parties sign.</summary>
        public string ContractHash { get; set; } = string.Empty;
        /// <summary>Deprecated: legacy message format; prefer signing ContractHash.</summary>
        public string MessageToSign { get; set; } = string.Empty;
        public string AppSignature { get; set; } = string.Empty;
        public string StorageNodeSignature { get; set; } = string.Empty;
        
        // Metadata
        public long CreatedAtUnix { get; set; }
        public string Status { get; set; } = "Active"; // "PendingAppApproval", "Active", "Completed", "Cancelled"
        public long? BlockchainSubmittedAtUnix { get; set; } = null;

        /// <summary>Options for canonical JSON: compact, no indentation. Must match Blocker.</summary>
        private static readonly JsonSerializerOptions CanonicalJsonOptions = new() { WriteIndented = false };

        /// <summary>
        /// Build the content object (canonical terms only) used for hashing and envelope.
        /// Returns a SortedDictionary that serializes to canonical JSON. Used by ComputeContractHash and envelope APIs.
        /// </summary>
        public static SortedDictionary<string, object> GetContractContent(StorageContract c)
        {
            var feeString = c.TotalFee.ToString("F10", CultureInfo.InvariantCulture);
            var fileIds = c.FileIds?.ToList() ?? new List<string>();
            fileIds.Sort(StringComparer.Ordinal);
            var dict = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["appPublicKey"] = c.AppPublicKey ?? "",
                ["contractId"] = c.ContractId ?? "",
                ["contractType"] = c.ContractType ?? "",
                ["createdAtUnix"] = c.CreatedAtUnix,
                ["fileIds"] = fileIds,
                ["startDateUnix"] = c.StartDateUnix,
                ["status"] = c.Status ?? "Active",
                ["storageNodePublicKey"] = c.StorageNodePublicKey ?? "",
                ["totalFee"] = feeString,
                ["totalFileSize"] = c.TotalFileSize
            };
            if (c.EndDateUnix.HasValue)
                dict["endDateUnix"] = c.EndDateUnix.Value;
            return dict;
        }

        /// <summary>
        /// Compute contract hash: SHA-256(utf8(canonicalJson)) as lowercase hex.
        /// Canonical JSON: alphabetical keys, content only (no hash/signatures), totalFee as F10 string, fileIds sorted.
        /// Must match Dart and Blocker implementation for cross-platform verification.
        /// </summary>
        public static string ComputeContractHash(StorageContract c)
        {
            var content = GetContractContent(c);
            var json = JsonSerializer.Serialize(content, CanonicalJsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Generate the message that both parties must sign (deprecated; use ContractHash).
        /// Format: TimeFixed|startDate|endDate|appPublicKey|storageNodePublicKey|totalFee|totalFileSize
        /// </summary>
        public static string GenerateMessageToSign(
            string contractType,
            long startDate,
            long? endDate,
            string appPublicKey,
            string storageNodePublicKey,
            decimal totalFee,
            long totalFileSize)
        {
            if (contractType == "TimeFixed")
            {
                if (!endDate.HasValue)
                    throw new ArgumentException("TimeFixed contracts require endDate");
                // Format fee as integer string (10 decimal places) to match Dart formatting
                // 🔧 FIX: Use InvariantCulture to ensure dot (.) is used as decimal separator, matching Dart/Blocker
                var feeString = totalFee.ToString("F10", System.Globalization.CultureInfo.InvariantCulture);
                return $"TimeFixed|{startDate}|{endDate}|{appPublicKey}|{storageNodePublicKey}|{feeString}|{totalFileSize}";
            }
            else if (contractType == "OpenEnded")
            {
                // Format fee as integer string (10 decimal places) to match Dart formatting
                // 🔧 FIX: Use InvariantCulture to ensure dot (.) is used as decimal separator, matching Dart/Blocker
                var feeString = totalFee.ToString("F10", System.Globalization.CultureInfo.InvariantCulture);
                return $"OpenEnded|{startDate}|{appPublicKey}|{storageNodePublicKey}|{feeString}|{totalFileSize}";
            }
            else
            {
                throw new ArgumentException($"Unknown contract type: {contractType}");
            }
        }

        /// <summary>
        /// Check if contract is currently valid (covers the current time).
        /// </summary>
        public bool IsValidNow()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            if (Status != "Active")
                return false;
            
            if (now < StartDateUnix)
                return false; // Not yet started
            
            if (ContractType == "TimeFixed" && EndDateUnix.HasValue && now > EndDateUnix)
                return false; // Contract expired
            
            if (ContractType == "OpenEnded" && now > StartDateUnix + (365 * 24 * 60 * 60))
                return false; // OpenEnded auto-expires after 1 year
            
            return true;
        }

        /// <summary>
        /// Check if a file ID is covered by this contract.
        /// </summary>
        public bool CoversFileId(string fileId)
        {
            return FileIds.Contains(fileId);
        }

        /// <summary>
        /// Check if a file size is within contract limits.
        /// </summary>
        public bool CoversFileSize(long fileSizeBytes)
        {
            return fileSizeBytes <= TotalFileSize;
        }
    }
}
