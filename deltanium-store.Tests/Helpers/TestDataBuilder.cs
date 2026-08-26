using System;
using System.Text;
using DeltaniumStore.Models;
using NBitcoin;

namespace deltanium_store.Tests.Helpers
{
    /// <summary>
    /// Helper class for generating test data
    /// </summary>
    public static class TestDataBuilder
    {
        /// <summary>
        /// Creates a valid FileMetadata for testing
        /// </summary>
        public static FileMetadata CreateFileMetadata(
            string? fileId = null,
            string? recipientPubKey = null,
            string? ownerPubKey = null,
            string? type = null,
            string? parentFileId = null,
            bool isPublic = false,
            string? shareType = null,
            string? encryptedType = null,
            string? policyTag = null,
            string? firstBlockId = null,
            string? kind = null,
            string? capsuleFor = null,
            string? policyScheme = null)
        {
            var (_, pk) = GenerateKeyPair();
            return new FileMetadata
            {
                FileId = fileId ?? Guid.NewGuid().ToString(),
                RecipientPubKey = recipientPubKey ?? pk,
                OwnerPubKey = ownerPubKey,
                Type = type ?? "file",
                ParentFileId = parentFileId,
                IsPublic = isPublic,
                ShareType = shareType ?? "me",
                EncryptedType = encryptedType ?? (isPublic ? "public" : "encrypted"),
                PolicyTag = policyTag,
                CapsuleFor = capsuleFor,
                PolicyScheme = policyScheme,
                FirstBlockId = firstBlockId ?? Guid.NewGuid().ToString(),
                EncryptedKey = Encoding.UTF8.GetBytes("test_encrypted_key"),
                Kind = kind,
                CreationTime = DateTime.UtcNow,
                Version = "2.0"
            };
        }

        /// <summary>
        /// Creates a valid BlockMetadata for testing
        /// </summary>
        public static BlockMetadata CreateBlockMetadata(
            string? blockId = null,
            long? blockSize = null,
            int blockIndex = 0)
        {
            return new BlockMetadata
            {
                BlockId = blockId ?? Guid.NewGuid().ToString(),
                BlockSize = blockSize ?? 1024,
                BlockIndex = blockIndex,
                BlockHash = "test_hash_" + Guid.NewGuid().ToString("N"),
                CreationTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Generates a key pair for testing
        /// </summary>
        public static (string mnemonic, string publicKey) GenerateKeyPair()
        {
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var hdRoot = mnemonic.DeriveExtKey();
            var privateKey = hdRoot.PrivateKey;
            var publicKey = privateKey.PubKey;
            return (mnemonic.ToString(), publicKey.ToHex());
        }

        /// <summary>
        /// Creates a valid compressed public key (66 chars)
        /// </summary>
        public static string CreateCompressedPublicKey()
        {
            var (_, pk) = GenerateKeyPair();
            var pubKey = new PubKey(pk);
            return pubKey.ToString(); // Returns compressed format
        }

        /// <summary>
        /// Creates a valid uncompressed public key (130 chars)
        /// </summary>
        public static string CreateUncompressedPublicKey()
        {
            var (_, pk) = GenerateKeyPair();
            var pubKey = new PubKey(pk);
            return pubKey.ToHex(); // Returns uncompressed format
        }

        /// <summary>
        /// Creates an invalid public key for negative testing
        /// </summary>
        public static string CreateInvalidPublicKey()
        {
            return "invalid_public_key_" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Creates a test memory stream with data
        /// </summary>
        public static MemoryStream CreateTestStream(string content)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }

        /// <summary>
        /// Creates a test memory stream with binary data
        /// </summary>
        public static MemoryStream CreateTestStream(byte[] data)
        {
            return new MemoryStream(data);
        }

        /// <summary>
        /// Creates a StorageContract for testing (envelope / GetContractContent / ComputeContractHash).
        /// </summary>
        public static StorageContract CreateStorageContract(
            string? contractId = null,
            string? appPublicKey = null,
            string? storageNodePublicKey = null,
            long startDateUnix = 0,
            long? endDateUnix = null,
            string contractType = "OpenEnded",
            decimal totalFee = 0.0000001000m,
            long totalFileSize = 1024,
            List<string>? fileIds = null)
        {
            var (_, pkApp) = GenerateKeyPair();
            var (__, pkNode) = GenerateKeyPair();
            var start = startDateUnix > 0 ? startDateUnix : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var end = endDateUnix ?? (contractType == "TimeFixed" ? start + 86400 * 30 : (long?)null);
            return new StorageContract
            {
                ContractId = contractId ?? Guid.NewGuid().ToString("N"),
                ContractType = contractType,
                AppPublicKey = appPublicKey ?? pkApp,
                StorageNodePublicKey = storageNodePublicKey ?? pkNode,
                StartDateUnix = start,
                EndDateUnix = end,
                TotalFileSize = totalFileSize,
                TotalFee = totalFee,
                FileIds = fileIds ?? new List<string> { Guid.NewGuid().ToString("N") },
                ContractHash = string.Empty,
                MessageToSign = string.Empty,
                AppSignature = "test-app-sig",
                StorageNodeSignature = "test-node-sig",
                CreatedAtUnix = start,
                Status = "Active"
            };
        }
    }
}

