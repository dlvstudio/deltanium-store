using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeltaniumStore.Models
{
    /// <summary>
    /// ZERO-KNOWLEDGE FileMetadata - Contains only non-sensitive information
    /// Sensitive data (fileName, fileSize, mimeType, etc.) is stored in encrypted metadata block
    /// </summary>
    public class FileMetadata
    {
        public string FileId { get; set; } = string.Empty;
        
        // 🔒 BACKWARD COMPATIBILITY: Optional sensitive fields (ignored in zero-knowledge mode)
        // These fields are now stored in encrypted metadata block, but kept for JSON compatibility
        [JsonPropertyName("fileName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FileName { get; set; } = null; // ❌ Legacy field - now in encrypted metadata block
        
        [JsonPropertyName("fileSize")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? FileSize { get; set; } = null; // ❌ Legacy field - now in encrypted metadata block
        
        [JsonPropertyName("mimeType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MimeType { get; set; } = null; // ❌ Legacy field - now in encrypted metadata block
        
        [JsonPropertyName("fileExtension")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FileExtension { get; set; } = null; // ❌ Legacy field - now in encrypted metadata block
        
        public DateTime CreationTime { get; set; }
        public string RecipientPubKey { get; set; } = string.Empty;
        
        // 🆕 File type (non-sensitive) - allows store to filter posts vs files
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; } = null; // "post", "file", "image", etc.
        
        // 🆕 Reaction linkage - non-sensitive for indexing
        [JsonPropertyName("parentFileId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParentFileId { get; set; } = null; // FileId of the original post

        // 🆕 Reaction kind - non-sensitive for counting (e.g., like/love/...)
        [JsonPropertyName("kind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; set; } = null;

        // 🆕 Public file flag - indicates if file is stored without encryption
        [JsonPropertyName("isPublic")]
        public bool IsPublic { get; set; } = false; // true for public posts, false for encrypted files
        
        // 🆕 ZERO-KNOWLEDGE SHARING: Each user gets unique firstBlockId (their block 0)
        // This prevents linking users who have access to the same file
        public string FirstBlockId { get; set; } = string.Empty; // Unique block 0 ID for this user
        
        // 🔒 ZERO-KNOWLEDGE SHARING: Single encrypted key per metadata entry
        // Each user gets their own metadata entry with their own encrypted key
        // This prevents storage nodes from knowing sharing relationships
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>(); // Single encrypted symmetric key for this user
        
        // Metadata for zero-knowledge implementation
        public string Version { get; set; } = "2.0"; // Schema version for compatibility
        
        // 🆕 Share type: public/shared/following
        public string ShareType { get; set; } = "me"; // 'followers', 'me', 'specific'
        
        public string EncryptedType { get; set; } = "encrypted"; // 'public' hoặc 'encrypted'

        // PRE (Option 2) fields - optional
        [JsonPropertyName("policyTag")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PolicyTag { get; set; } = null; // e.g., followers:2025

        [JsonPropertyName("capsuleFor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CapsuleFor { get; set; } = null; // "tag" when using PRE

        [JsonPropertyName("policyScheme")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PolicyScheme { get; set; } = null; // e.g., CPRE

        // 🆕 Owner public key - needed for PRE posts to identify author in feeds
        [JsonPropertyName("ownerPubKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OwnerPubKey { get; set; } = null; // Author's public key for PRE posts

        // 🆕 STORAGE CONTRACT TRACKING
        /// <summary>
        /// Contract ID that covers this file upload.
        /// StorageNode validates uploads against signed StorageContract before creating StorageOperation.
        /// </summary>
        [JsonPropertyName("contractId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContractId { get; set; } = null;

        /// <summary>
        /// Contract status for this file: "Pending", "Active", "Completed", "Cancelled".
        /// </summary>
        [JsonPropertyName("contractStatus")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContractStatus { get; set; } = "Active";

        /// <summary>
        /// Contract start date (Unix timestamp) - when storage obligation begins.
        /// </summary>
        [JsonPropertyName("contractStartDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ContractStartDate { get; set; } = null;

        /// <summary>
        /// Contract end date (Unix timestamp) for TimeFixed contracts; null for OpenEnded.
        /// </summary>
        [JsonPropertyName("contractEndDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ContractEndDate { get; set; } = null;

        // 🆕 HYBRID PRE: PRE capsule for followers (while encryptedKey is ECIES for author)
        [JsonPropertyName("encapsulatedForRecipient")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[]? EncapsulatedForRecipient { get; set; } = null; // PRE capsule for followers to re-encrypt
    }
}