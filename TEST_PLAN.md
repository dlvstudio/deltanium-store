# Unit Test Plan cho deltanium-store

## Tổng quan

Kế hoạch này mô tả chi tiết các unit tests cần được viết cho project `deltanium-store`, một Content Store Node trong hệ thống Deltanium blockchain.

## Mục tiêu

- Đảm bảo tất cả services và controllers hoạt động đúng
- Đạt coverage tối thiểu 70% cho các services quan trọng
- Phát hiện bugs sớm trong quá trình development
- Tạo documentation thông qua tests

## Cấu trúc Test Project

```
deltanium-store.Tests/
├── Services/
│   ├── FileStorageServiceTests.cs
│   ├── CentralApiServiceTests.cs
│   └── PublicKeyUtilsTests.cs
├── Controllers/
│   ├── FileControllerTests.cs
│   └── PostControllerTests.cs
├── Helpers/
│   ├── TestDataBuilder.cs
│   └── TestFileHelper.cs
└── deltanium-store.Tests.csproj
```

## 1. Services Tests

### 1.1 FileStorageService Tests

**Priority: HIGH**

#### Test Cases:

1. **StoreFileMetadataAsync**
   - ✅ Should save new file metadata
   - ✅ Should normalize RecipientPubKey before saving
   - ✅ Should update cache after saving
   - ✅ Should handle invalid metadata gracefully
   - ✅ Should serialize with camelCase naming policy

2. **StoreBlockAsync**
   - ✅ Should save block data to disk
   - ✅ Should create block metadata
   - ✅ Should calculate block hash correctly
   - ✅ Should handle large blocks
   - ✅ Should cleanup on error

3. **GetBlockAsync**
   - ✅ Should return block stream for existing block
   - ✅ Should throw FileNotFoundException for non-existent block
   - ✅ Should handle concurrent access

4. **GetFileMetadataAsync**
   - ✅ Should return metadata for matching RecipientPubKey
   - ✅ Should return public posts regardless of RecipientPubKey
   - ✅ Should handle zero-knowledge sharing (find by OwnerPubKey)
   - ✅ Should handle compressed/uncompressed public keys
   - ✅ Should find related files by ParentFileId
   - ✅ Should throw FileNotFoundException when not found
   - ✅ Should normalize user public key before search

5. **ListUserFilesAsync**
   - ✅ Should return all files for user
   - ✅ Should normalize public key before search
   - ✅ Should handle empty results
   - ✅ Should handle compressed/uncompressed keys

6. **FileExistsAsync**
   - ✅ Should return true for existing file
   - ✅ Should return false for non-existent file
   - ✅ Should check cache first, then disk

7. **ListAllPostsByShareTypeAsync**
   - ✅ Should return posts with matching ShareType
   - ✅ Should filter by Type = "post"
   - ✅ Should handle empty results

8. **ListAllPublicPostsAsync**
   - ✅ Should return all public posts
   - ✅ Should filter by IsPublic or EncryptedType = "public"
   - ✅ Should handle empty results

9. **ListReactionsByParentAsync**
   - ✅ Should return reactions for parent file
   - ✅ Should filter by Type = "react"
   - ✅ Should handle empty results

10. **ListCommentsByParentAsync**
    - ✅ Should return comments for parent file
    - ✅ Should filter by Type = "comment"
    - ✅ Should deduplicate by FileId
    - ✅ Should order by CreationTime
    - ✅ Should handle empty results

11. **DeleteFileByFileIdAsync**
    - ✅ Should delete file metadata
    - ✅ Should remove from cache
    - ✅ Should delete associated block 0
    - ✅ Should return false for non-existent file
    - ✅ Should handle errors gracefully

12. **LoadMetadata**
    - ✅ Should load file metadata from disk
    - ✅ Should load block metadata from disk
    - ✅ Should handle missing directories
    - ✅ Should handle invalid JSON files
    - ✅ Should handle case-insensitive JSON

13. **EnsureStorageDirectories**
    - ✅ Should create directories if not exist
    - ✅ Should handle permission errors

### 1.2 CentralApiService Tests

**Priority: MEDIUM**

#### Test Cases:

1. **GetFollowingUsersAsync**
   - ✅ Should return list of following users
   - ✅ Should parse JSON response correctly
   - ✅ Should handle API errors gracefully
   - ✅ Should return empty list on failure
   - ✅ Should handle timeout

2. **GetUserInfoAsync**
   - ✅ Should return user info dictionary
   - ✅ Should handle API errors gracefully
   - ✅ Should return empty dictionary on failure

3. **ValidatePreTokenAsync**
   - ✅ Should return rk (base64) for valid token
   - ✅ Should return null for invalid token
   - ✅ Should handle API errors gracefully
   - ✅ Should parse JSON response correctly

4. **HttpClient Configuration**
   - ✅ Should use correct base URL
   - ✅ Should use correct timeout
   - ✅ Should handle configuration missing

### 1.3 PublicKeyUtils Tests

**Priority: HIGH**

#### Test Cases:

1. **ConvertToCompressedPublicKey**
   - ✅ Should return as-is if already compressed
   - ✅ Should convert uncompressed to compressed
   - ✅ Should handle invalid formats gracefully
   - ✅ Should handle edge cases

2. **NormalizePublicKey**
   - ✅ Should normalize to compressed format
   - ✅ Should throw exception for null/empty key
   - ✅ Should throw exception for invalid format
   - ✅ Should validate format before conversion

3. **AreKeysEqual**
   - ✅ Should return true for same compressed keys
   - ✅ Should return true for same uncompressed keys
   - ✅ Should return true for compressed vs uncompressed (same key)
   - ✅ Should return false for different keys
   - ✅ Should handle normalization errors gracefully

4. **IsValidPublicKey**
   - ✅ Should return true for valid compressed key
   - ✅ Should return true for valid uncompressed key
   - ✅ Should return false for null/empty
   - ✅ Should return false for invalid length
   - ✅ Should return false for non-hex characters
   - ✅ Should return false for invalid prefix

5. **GetUncompressedVersion**
   - ✅ Should return uncompressed for known mappings
   - ✅ Should return null for unknown keys

## 2. Controllers Tests

### 2.1 FileController Tests

**Priority: HIGH**

#### Test Cases:

1. **UploadFile (multipart)**
   - ✅ Should upload metadata file
   - ✅ Should upload block file
   - ✅ Should validate user public key header
   - ✅ Should reject null/empty file
   - ✅ Should validate FileId in metadata
   - ✅ Should validate FirstBlockId in metadata
   - ✅ Should return correct response format
   - ✅ Should handle errors gracefully

2. **DownloadFile**
   - ✅ Should return file metadata
   - ✅ Should validate user public key header
   - ✅ Should return 404 for non-existent file
   - ✅ Should return 401 for unauthorized access
   - ✅ Should handle errors gracefully

3. **DownloadBlock**
   - ✅ Should return block stream
   - ✅ Should return 404 for non-existent block
   - ✅ Should handle errors gracefully

4. **DownloadBlockByIndex**
   - ✅ Should return block 0 using FirstBlockId
   - ✅ Should validate user public key header
   - ✅ Should return 400 for content blocks (index > 0)
   - ✅ Should return 404 for non-existent file
   - ✅ Should return 401 for unauthorized access

5. **ListFiles**
   - ✅ Should return user's files
   - ✅ Should validate user public key header
   - ✅ Should handle empty results
   - ✅ Should handle errors gracefully

6. **CheckFileExists**
   - ✅ Should return exists status
   - ✅ Should handle errors gracefully

7. **UploadMetadataRaw**
   - ✅ Should accept raw JSON body
   - ✅ Should validate FileId
   - ✅ Should validate FirstBlockId
   - ✅ Should normalize PRE fields
   - ✅ Should set OwnerPubKey for reactions
   - ✅ Should validate comment ParentFileId
   - ✅ Should validate comment parent post exists
   - ✅ Should validate comment parent is post type
   - ✅ Should validate encrypted comment RecipientPubKey matches post owner
   - ✅ Should handle errors gracefully

8. **UploadFileBlockRaw**
   - ✅ Should accept raw binary body
   - ✅ Should validate headers (X-Block-Id, X-File-Id, X-Block-Index)
   - ✅ Should validate file metadata exists
   - ✅ Should handle errors gracefully

9. **ReencryptForFollowerAsync**
   - ✅ Should re-encrypt PRE bundle for follower
   - ✅ Should validate fileId and policyTag
   - ✅ Should validate PRE-tagged entry
   - ✅ Should validate token with Central API
   - ✅ Should perform PRE re-encryption
   - ✅ Should handle errors gracefully

10. **ListPosts**
    - ✅ Should return user's posts
    - ✅ Should filter by Type = "post"
    - ✅ Should exclude followers-only posts
    - ✅ Should validate user public key header

11. **GetTimeline**
    - ✅ Should return user's files
    - ✅ Should validate user public key header

### 2.2 PostController Tests

**Priority: MEDIUM**

#### Test Cases:

1. **GetReactions**
   - ✅ Should return reaction counts by kind
   - ✅ Should return total count
   - ✅ Should return user's reaction
   - ✅ Should handle empty results

2. **RemoveReaction**
   - ✅ Should remove user's reaction
   - ✅ Should filter by kind if provided
   - ✅ Should validate user public key header
   - ✅ Should return count of removed reactions

3. **GetComments**
   - ✅ Should return accessible comments
   - ✅ Should filter public comments
   - ✅ Should filter encrypted comments for user
   - ✅ Should return correct format
   - ✅ Should handle empty results

4. **DeleteComment**
   - ✅ Should delete comment by owner
   - ✅ Should return 403 for non-owner
   - ✅ Should return 404 for non-existent comment
   - ✅ Should validate user public key header

5. **GetUserPosts**
   - ✅ Should return user's posts
   - ✅ Should filter by Type = "post"
   - ✅ Should return correct format

6. **GetTimeline**
   - ✅ Should return user's posts
   - ✅ Should validate user public key header

7. **GetFollowingFeed**
   - ✅ Should return posts from followed users
   - ✅ Should filter accessible posts
   - ✅ Should sort by creation time
   - ✅ Should apply limit
   - ✅ Should handle no following users
   - ✅ Should handle API errors gracefully

8. **GetDetailedFollowingFeed**
   - ✅ Should return posts with full content
   - ✅ Should include author info
   - ✅ Should handle content loading errors

9. **GetFollowersFeed**
   - ✅ Should return posts with ShareType = "followers"
   - ✅ Should union Option 1 & Option 2
   - ✅ Should sort by creation time
   - ✅ Should apply limit

10. **GetPublicFeed**
    - ✅ Should return all public posts
    - ✅ Should sort by creation time
    - ✅ Should apply limit

11. **TestCentralAPI**
    - ✅ Should test following users fetch
    - ✅ Should test user info fetch
    - ✅ Should return test results

12. **GetFollowingStores**
    - ✅ Should return list of stores
    - ✅ Should return following users
    - ✅ Should validate user public key header

13. **CanUserAccessPost (private method)**
    - ✅ Should return true for public posts
    - ✅ Should check encrypted keys for private posts
    - ✅ Should handle errors gracefully

## 3. Test Helpers

### 3.1 TestDataBuilder

Cần tạo helper methods để generate test data:

- `CreateFileMetadata()` - Tạo FileMetadata với các options
- `CreateBlockMetadata()` - Tạo BlockMetadata
- `CreateCompressedPublicKey()` - Tạo compressed public key
- `CreateUncompressedPublicKey()` - Tạo uncompressed public key
- `CreateInvalidPublicKey()` - Tạo invalid public key
- `CreateMemoryStream()` - Tạo test stream với data

### 3.2 TestFileHelper

Cần helper để quản lý temporary files:

- `CreateTempDirectory()` - Tạo temp directory
- `CleanupTempFiles()` - Cleanup sau tests
- `CreateTestMetadataFile()` - Tạo test metadata file
- `CreateTestBlockFile()` - Tạo test block file

## 4. Dependencies

### NuGet Packages cần thêm:

- `xunit` - Test framework
- `xunit.runner.visualstudio` - Test runner
- `Moq` - Mocking framework
- `FluentAssertions` - Assertion library
- `AutoFixture` - Test data generation
- `AutoFixture.Xunit2` - AutoFixture integration
- `Microsoft.AspNetCore.Mvc.Testing` - Integration testing
- `coverlet.collector` - Code coverage
- `Microsoft.NET.Test.Sdk` - Test SDK

## 5. Test Execution Strategy

### Phase 1: Services Tests (Priority: HIGH)
1. PublicKeyUtilsTests - Utility class, dễ test
2. FileStorageServiceTests - Core service, nhiều logic
3. CentralApiServiceTests - External dependency, cần mocking

### Phase 2: Controllers Tests (Priority: MEDIUM)
1. FileControllerTests - Core file operations
2. PostControllerTests - Social features

### Phase 3: Integration Tests (Priority: LOW)
1. End-to-end file upload/download flow
2. Post creation and feed retrieval
3. Comment and reaction flows

## 6. Coverage Goals

- **FileStorageService**: 80%+ line coverage
- **PublicKeyUtils**: 100% line coverage
- **CentralApiService**: 70%+ line coverage (với mocking)
- **FileController**: 70%+ line coverage
- **PostController**: 60%+ line coverage

## 7. Notes

- Sử dụng Moq để mock `ICentralApiService` và `IFileStorageService`
- Sử dụng `TestFileHelper` để quản lý temporary storage directories
- Tests cần cleanup sau khi chạy để tránh conflicts
- Cần test cả compressed và uncompressed public keys
- Cần test zero-knowledge sharing scenarios
- Cần test PRE (Proxy Re-Encryption) flows

## 8. Estimated Test Count

- **Services**: ~50 tests
- **Controllers**: ~60 tests
- **Total**: ~110 tests

## 9. Next Steps

1. Tạo test project structure
2. Setup dependencies và configuration
3. Implement TestDataBuilder và TestFileHelper
4. Implement Services tests (Phase 1)
5. Implement Controllers tests (Phase 2)
6. Run tests và check coverage
7. Fix bugs và improve coverage
8. Add integration tests (Phase 3)








