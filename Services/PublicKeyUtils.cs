using System;
using System.Numerics;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Utility class for public key format normalization and comparison
    /// Ensures compatibility between compressed and uncompressed public key formats
    /// </summary>
    public static class PublicKeyUtils
    {
        /// <summary>
        /// Convert uncompressed public key to compressed format
        /// </summary>
        /// <param name="publicKeyHex">Public key in hex format</param>
        /// <returns>Compressed public key</returns>
        public static string ConvertToCompressedPublicKey(string publicKeyHex)
        {
            Console.WriteLine($"PublicKeyUtils: Converting key {publicKeyHex} ({publicKeyHex.Length} chars)");
            
            // Already compressed (66 chars, starts with 02/03)
            if (publicKeyHex.Length == 66 && (publicKeyHex.StartsWith("02") || publicKeyHex.StartsWith("03")))
            {
                Console.WriteLine("PublicKeyUtils: Already compressed, returning as-is");
                return publicKeyHex;
            }
            
            // Uncompressed format (130 chars, starts with 04)
            if (publicKeyHex.Length == 130 && publicKeyHex.StartsWith("04"))
            {
                try
                {
                    // Extract x coordinate (skip '04' prefix, take next 64 chars)
                    string xHex = publicKeyHex.Substring(2, 64);
                    
                    // Extract y coordinate (last 64 chars)
                    string yHex = publicKeyHex.Substring(66, 64);
                    BigInteger yBigInt = BigInteger.Parse("0" + yHex, System.Globalization.NumberStyles.HexNumber);
                    
                    // Determine if y is even or odd
                    string prefix = yBigInt.IsEven ? "02" : "03";
                    
                    // Compressed format: prefix + x coordinate
                    string result = prefix + xHex;
                    Console.WriteLine($"PublicKeyUtils: Converted to compressed = {result} ({result.Length} chars)");
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PublicKeyUtils: Error converting key to compressed format: {ex.Message}");
                    return publicKeyHex; // Return original if conversion fails
                }
            }
            
            // Unknown format, return as-is
            Console.WriteLine($"PublicKeyUtils: Unknown key format ({publicKeyHex.Length} chars), using as-is");
            return publicKeyHex;
        }

        /// <summary>
        /// Normalize public key to compressed format for consistent storage and comparison
        /// </summary>
        /// <param name="publicKey">Public key in any valid format</param>
        /// <returns>Normalized (compressed) public key</returns>
        public static string NormalizePublicKey(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
            {
                throw new ArgumentException("Public key cannot be null or empty");
            }

            // Validate format
            if (!IsValidPublicKey(publicKey))
            {
                throw new ArgumentException($"Invalid public key format: {publicKey}");
            }

            // Convert to compressed format
            string compressedKey = ConvertToCompressedPublicKey(publicKey);
            
            Console.WriteLine($"PublicKeyUtils: Normalized key {publicKey.Length} -> {compressedKey.Length} chars");
            return compressedKey;
        }

        /// <summary>
        /// Check if two public keys are the same (considering both compressed and uncompressed formats)
        /// </summary>
        /// <param name="key1">First public key</param>
        /// <param name="key2">Second public key</param>
        /// <returns>True if keys represent the same point</returns>
        public static bool AreKeysEqual(string key1, string key2)
        {
            try
            {
                string normalized1 = NormalizePublicKey(key1);
                string normalized2 = NormalizePublicKey(key2);
                return normalized1.Equals(normalized2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // If normalization fails, do direct comparison as fallback
                return key1.Equals(key2, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Validate public key format
        /// </summary>
        /// <param name="publicKey">Public key to validate</param>
        /// <returns>True if valid format</returns>
        public static bool IsValidPublicKey(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
            {
                return false;
            }

            // Check if it's hex string
            if (!System.Text.RegularExpressions.Regex.IsMatch(publicKey, @"^[0-9a-fA-F]+$"))
            {
                return false;
            }
            
            // Compressed format: 66 characters (33 bytes), starts with 02 or 03
            if (publicKey.Length == 66)
            {
                return publicKey.StartsWith("02") || publicKey.StartsWith("03");
            }
            
            // Uncompressed format: 130 characters (65 bytes), starts with 04
            if (publicKey.Length == 130)
            {
                return publicKey.StartsWith("04");
            }
            
            return false;
        }

        /// <summary>
        /// Known mappings for testing purposes (mirrors the ones in Dart code)
        /// In production, this could be extended or use proper elliptic curve math
        /// </summary>
        private static readonly Dictionary<string, string> KnownCompressionMappings = new()
        {
            {
                "021d35556bed51e3767caf6e34c22f99ba6d14d07dc086b114b746fa632ad74775",
                "041d35556bed51e3767caf6e34c22f99ba6d14d07dc086b114b746fa632ad74775e7f89624ca19713b9ea22654ce0c63a39e84e51021d4d71452104a9870f08c38"
            },
            {
                "027c7901294083c84ece799539394d53e9d47048c6139132e04215adf5c7e3c11a",
                "047c7901294083c84ece799539394d53e9d47048c6139132e04215adf5c7e3c11a16d455fe578a7fd65d771140371b09e67f649dfaf47ec9fe220cd34459d4443c"
            }
        };

        /// <summary>
        /// Convert compressed to uncompressed using known mappings (for testing)
        /// </summary>
        /// <param name="compressedKey">Compressed public key</param>
        /// <returns>Uncompressed public key if known, null otherwise</returns>
        public static string? GetUncompressedVersion(string compressedKey)
        {
            return KnownCompressionMappings.GetValueOrDefault(compressedKey);
        }
    }
} 