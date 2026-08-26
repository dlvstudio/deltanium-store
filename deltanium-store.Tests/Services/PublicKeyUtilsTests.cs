using FluentAssertions;
using Xunit;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;

namespace deltanium_store.Tests.Services
{
    public class PublicKeyUtilsTests
    {
        [Fact]
        public void ConvertToCompressedPublicKey_AlreadyCompressed_ShouldReturnAsIs()
        {
            // Arrange
            var compressedKey = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = PublicKeyUtils.ConvertToCompressedPublicKey(compressedKey);

            // Assert
            result.Should().Be(compressedKey);
            result.Length.Should().Be(66);
            (result.StartsWith("02") || result.StartsWith("03")).Should().BeTrue();
        }

        [Fact]
        public void ConvertToCompressedPublicKey_Uncompressed_ShouldConvert()
        {
            // Arrange
            var uncompressedKey = TestDataBuilder.CreateUncompressedPublicKey();

            // Act
            var result = PublicKeyUtils.ConvertToCompressedPublicKey(uncompressedKey);

            // Assert
            result.Length.Should().Be(66);
            (result.StartsWith("02") || result.StartsWith("03")).Should().BeTrue();
        }

        [Fact]
        public void ConvertToCompressedPublicKey_InvalidFormat_ShouldReturnAsIs()
        {
            // Arrange
            var invalidKey = TestDataBuilder.CreateInvalidPublicKey();

            // Act
            var result = PublicKeyUtils.ConvertToCompressedPublicKey(invalidKey);

            // Assert
            result.Should().Be(invalidKey);
        }

        [Fact]
        public void NormalizePublicKey_CompressedKey_ShouldReturnCompressed()
        {
            // Arrange
            var compressedKey = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = PublicKeyUtils.NormalizePublicKey(compressedKey);

            // Assert
            result.Should().Be(compressedKey);
            result.Length.Should().Be(66);
        }

        [Fact]
        public void NormalizePublicKey_UncompressedKey_ShouldReturnCompressed()
        {
            // Arrange
            var uncompressedKey = TestDataBuilder.CreateUncompressedPublicKey();

            // Act
            var result = PublicKeyUtils.NormalizePublicKey(uncompressedKey);

            // Assert
            result.Length.Should().Be(66);
            (result.StartsWith("02") || result.StartsWith("03")).Should().BeTrue();
        }

        [Fact]
        public void NormalizePublicKey_NullKey_ShouldThrowException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() => PublicKeyUtils.NormalizePublicKey(null!));
        }

        [Fact]
        public void NormalizePublicKey_EmptyKey_ShouldThrowException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() => PublicKeyUtils.NormalizePublicKey(string.Empty));
        }

        [Fact]
        public void NormalizePublicKey_InvalidKey_ShouldThrowException()
        {
            // Arrange
            var invalidKey = TestDataBuilder.CreateInvalidPublicKey();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => PublicKeyUtils.NormalizePublicKey(invalidKey));
        }

        [Fact]
        public void AreKeysEqual_SameCompressedKeys_ShouldReturnTrue()
        {
            // Arrange
            var key1 = TestDataBuilder.CreateCompressedPublicKey();
            var key2 = key1; // Same key

            // Act
            var result = PublicKeyUtils.AreKeysEqual(key1, key2);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void AreKeysEqual_SameUncompressedKeys_ShouldReturnTrue()
        {
            // Arrange
            var key1 = TestDataBuilder.CreateUncompressedPublicKey();
            var key2 = key1; // Same key

            // Act
            var result = PublicKeyUtils.AreKeysEqual(key1, key2);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void AreKeysEqual_CompressedAndUncompressed_SameKey_ShouldReturnTrue()
        {
            // Arrange
            var (_, uncompressedKey) = TestDataBuilder.GenerateKeyPair();
            var pubKey = new NBitcoin.PubKey(uncompressedKey);
            var compressedKey = pubKey.ToString();

            // Act
            var result = PublicKeyUtils.AreKeysEqual(compressedKey, uncompressedKey);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void AreKeysEqual_DifferentKeys_ShouldReturnFalse()
        {
            // Arrange
            var key1 = TestDataBuilder.CreateCompressedPublicKey();
            var key2 = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = PublicKeyUtils.AreKeysEqual(key1, key2);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void AreKeysEqual_InvalidKeys_ShouldFallbackToDirectComparison()
        {
            // Arrange
            var key1 = "invalid_key_1";
            var key2 = "invalid_key_1";

            // Act
            var result = PublicKeyUtils.AreKeysEqual(key1, key2);

            // Assert
            result.Should().BeTrue(); // Direct comparison should work
        }

        [Fact]
        public void IsValidPublicKey_ValidCompressedKey_ShouldReturnTrue()
        {
            // Arrange
            var key = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = PublicKeyUtils.IsValidPublicKey(key);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void IsValidPublicKey_ValidUncompressedKey_ShouldReturnTrue()
        {
            // Arrange
            var key = TestDataBuilder.CreateUncompressedPublicKey();

            // Act
            var result = PublicKeyUtils.IsValidPublicKey(key);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void IsValidPublicKey_NullKey_ShouldReturnFalse()
        {
            // Act
            var result = PublicKeyUtils.IsValidPublicKey(null!);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsValidPublicKey_EmptyKey_ShouldReturnFalse()
        {
            // Act
            var result = PublicKeyUtils.IsValidPublicKey(string.Empty);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsValidPublicKey_InvalidLength_ShouldReturnFalse()
        {
            // Arrange
            var key = "02" + new string('a', 50); // Wrong length

            // Act
            var result = PublicKeyUtils.IsValidPublicKey(key);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsValidPublicKey_NonHexCharacters_ShouldReturnFalse()
        {
            // Arrange
            var key = "02" + new string('g', 64); // Contains non-hex characters

            // Act
            var result = PublicKeyUtils.IsValidPublicKey(key);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void IsValidPublicKey_InvalidPrefix_ShouldReturnFalse()
        {
            // Arrange
            var key = "01" + new string('a', 64); // Invalid prefix

            // Act
            var result = PublicKeyUtils.IsValidPublicKey(key);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void GetUncompressedVersion_KnownMapping_ShouldReturnUncompressed()
        {
            // Arrange
            var compressedKey = "021d35556bed51e3767caf6e34c22f99ba6d14d07dc086b114b746fa632ad74775";

            // Act
            var result = PublicKeyUtils.GetUncompressedVersion(compressedKey);

            // Assert
            result.Should().NotBeNull();
            result!.Length.Should().Be(130);
            result.Should().StartWith("04");
        }

        [Fact]
        public void GetUncompressedVersion_UnknownKey_ShouldReturnNull()
        {
            // Arrange
            var unknownKey = TestDataBuilder.CreateCompressedPublicKey();

            // Act
            var result = PublicKeyUtils.GetUncompressedVersion(unknownKey);

            // Assert
            result.Should().BeNull();
        }
    }
}

