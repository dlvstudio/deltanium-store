using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using System.Text.Json;
using System.Linq;
using Serilog;

namespace DeltaniumStore.Services
{
    public interface IFileStorageService
    {
        Task<FileMetadata> StoreFileMetadataAsync(FileMetadata metadata);
        Task<BlockMetadata> StoreBlockAsync(Stream blockStream, string blockId, int blockIndex);
        Task<Stream> GetBlockAsync(string blockId);
        Task<FileMetadata> GetFileMetadataAsync(string fileId, string userPubKey);
        Task<List<FileMetadata>> ListUserFilesAsync(string pubKey);
        Task<bool> FileExistsAsync(string fileId);
        Task<List<FileMetadata>> ListAllPostsByShareTypeAsync(string shareType);
        Task<List<FileMetadata>> ListAllPublicPostsAsync();
        Task<List<FileMetadata>> ListReactionsByParentAsync(string parentFileId);
        Task<List<FileMetadata>> ListCommentsByParentAsync(string parentFileId);
        Task<bool> DeleteFileByFileIdAsync(string fileId);
    }

    public class FileStorageService : IFileStorageService
    {
        private readonly string _storageBasePath;
        private readonly string _fileMetadataPath;
        private readonly string _blockMetadataPath;
        private readonly string _blockDataPath;
        private Dictionary<string, FileMetadata> _fileMetadataCache;
        private Dictionary<string, BlockMetadata> _blockMetadataCache;

        public FileStorageService(string storageBasePath)
        {
            _storageBasePath = storageBasePath;
            _fileMetadataPath = Path.Combine(_storageBasePath, "file_metadata");
            _blockMetadataPath = Path.Combine(_storageBasePath, "block_metadata");
            _blockDataPath = Path.Combine(_storageBasePath, "block_data");
            
            _fileMetadataCache = new Dictionary<string, FileMetadata>();
            _blockMetadataCache = new Dictionary<string, BlockMetadata>();
            
            // Create directories and load metadata
            EnsureStorageDirectories();
            LoadMetadata();
        }

        private void EnsureStorageDirectories()
        {
            try
            {
                Directory.CreateDirectory(_storageBasePath);
                Directory.CreateDirectory(_fileMetadataPath);
                Directory.CreateDirectory(_blockMetadataPath);
                Directory.CreateDirectory(_blockDataPath);
                
                Console.WriteLine($"Storage directories ensured at: {_storageBasePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating storage directories: {ex.Message}");
                throw;
            }
        }

        private void LoadMetadata()
        {
            try
            {
                EnsureStorageDirectories();

                // Use case-insensitive deserialization to handle both camelCase and PascalCase JSON
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                // Load file metadata
                foreach (var file in Directory.GetFiles(_fileMetadataPath, "*.metadata.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var metadata = JsonSerializer.Deserialize<FileMetadata>(json, options);
                        if (metadata != null && !string.IsNullOrEmpty(metadata.FileId))
                        {
                            // Debug: Log ParentFileId for comments before caching
                            if (string.Equals(metadata.Type, "comment", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[LoadMetadata] Loading comment: FileId={metadata.FileId}, ParentFileId={metadata.ParentFileId ?? "(null)"}, Type={metadata.Type}");
                                // Also check raw JSON
                                if (json.Contains("parentFileId"))
                                {
                                    Console.WriteLine($"[LoadMetadata] JSON contains 'parentFileId' (camelCase)");
                                }
                                if (json.Contains("ParentFileId"))
                                {
                                    Console.WriteLine($"[LoadMetadata] JSON contains 'ParentFileId' (PascalCase)");
                                }
                            }
                            _fileMetadataCache[metadata.FileId] = metadata;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LoadMetadata] Error loading metadata from {file}: {ex.Message}");
                    }
                }

                // Load block metadata
                foreach (var file in Directory.GetFiles(_blockMetadataPath, "*.metadata.json"))
                {
                    var json = File.ReadAllText(file);
                    var metadata = JsonSerializer.Deserialize<BlockMetadata>(json, options);
                    if (metadata != null && !string.IsNullOrEmpty(metadata.BlockId))
                    {
                        _blockMetadataCache[metadata.BlockId] = metadata;
                    }
                }

                Console.WriteLine($"Loaded {_fileMetadataCache.Count} file metadata entries");
                Console.WriteLine($"Loaded {_blockMetadataCache.Count} block metadata entries");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading metadata: {ex.Message}");
                _fileMetadataCache = new Dictionary<string, FileMetadata>();
                _blockMetadataCache = new Dictionary<string, BlockMetadata>();
            }
        }

        public async Task<FileMetadata> StoreFileMetadataAsync(FileMetadata metadata)
        {
            try
            {
                EnsureStorageDirectories();
                
                // 🆕 NORMALIZE: Store with normalized public key format
                if (!string.IsNullOrEmpty(metadata.RecipientPubKey))
                {
                    var originalKey = metadata.RecipientPubKey;
                    metadata.RecipientPubKey = PublicKeyUtils.NormalizePublicKey(metadata.RecipientPubKey);
                    Console.WriteLine($"FileStorage: Normalized RecipientPubKey {originalKey} -> {metadata.RecipientPubKey}");
                }
                
                // Use camelCase naming policy to match Flutter's JSON format
                var serializeOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                };
                var json = JsonSerializer.Serialize(metadata, serializeOptions);
                var path = Path.Combine(_fileMetadataPath, $"{metadata.FileId}.metadata.json");
                await File.WriteAllTextAsync(path, json);
                // Ensure file is flushed to disk
                await File.WriteAllTextAsync(path, json); // Write twice to ensure flush
                _fileMetadataCache[metadata.FileId] = metadata;
                Console.WriteLine($"[StoreFileMetadataAsync] Stored file metadata for {metadata.FileId}, ParentFileId={metadata.ParentFileId ?? "(null)"}, Type={metadata.Type}, IsPublic={metadata.IsPublic}");
                return metadata;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving file metadata: {ex.Message}");
                throw;
            }
        }

        public async Task<BlockMetadata> StoreBlockAsync(Stream blockStream, string blockId, int blockIndex)
        {
            Console.WriteLine($"Storing anonymous block {blockId} (no file association for privacy)");
            
            try
            {
                EnsureStorageDirectories();

                // Store block data
                var blockDataPath = Path.Combine(_blockDataPath, $"{blockId}.data");
                using (var fileStream = File.Create(blockDataPath))
                {
                    await blockStream.CopyToAsync(fileStream);
                }

                // Create and store block metadata (without FileId for privacy)
                var blockMetadata = new BlockMetadata
                {
                    BlockId = blockId,
                    // FileId removed for privacy - blocks are now anonymous
                    BlockSize = blockStream.Length,
                    BlockIndex = blockIndex,
                    CreationTime = DateTime.UtcNow,
                    BlockHash = await CalculateBlockHashAsync(blockDataPath)
                };

                var json = JsonSerializer.Serialize(blockMetadata);
                var metadataPath = Path.Combine(_blockMetadataPath, $"{blockId}.metadata.json");
                await File.WriteAllTextAsync(metadataPath, json);
                _blockMetadataCache[blockId] = blockMetadata;

                // NOTE: Block IDs are now managed in file metadata only
                // This makes blocks completely anonymous at storage level

                Console.WriteLine($"Successfully stored anonymous block {blockId}");
                Console.WriteLine($"- Data path: {blockDataPath}");
                Console.WriteLine($"- Metadata path: {metadataPath}");
                return blockMetadata;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error storing block: {ex.Message}");
                // Cleanup if something goes wrong
                var blockDataPath = Path.Combine(_blockDataPath, $"{blockId}.data");
                if (File.Exists(blockDataPath))
                {
                    File.Delete(blockDataPath);
                }
                throw;
            }
        }

        private async Task<string> CalculateBlockHashAsync(string filePath)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = await sha256.ComputeHashAsync(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public async Task<Stream> GetBlockAsync(string blockId)
        {
            EnsureStorageDirectories();

            var blockDataPath = Path.Combine(_blockDataPath, $"{blockId}.data");
            Console.WriteLine($"Getting block data from: {blockDataPath}");

            if (!File.Exists(blockDataPath))
            {
                Console.WriteLine($"Block data not found at: {blockDataPath}");
                throw new FileNotFoundException($"Block data not found: {blockId}");
            }

            return await Task.FromResult(File.OpenRead(blockDataPath));
        }

        public async Task<FileMetadata> GetFileMetadataAsync(string fileId, string userPubKey)
        {
            var userPreview = userPubKey.Substring(0, Math.Min(20, userPubKey.Length));
            Log.Information($"[GetFileMetadataAsync] Getting file metadata: {fileId}, User: {userPreview}...");
            ManualLogger.Info($"[GetFileMetadataAsync] Getting file metadata: {fileId}, User: {userPreview}...");

            EnsureStorageDirectories();
            LoadMetadata(); // Ensure cache is up to date
            Log.Information($"[GetFileMetadataAsync] Cache has {_fileMetadataCache.Count} entries");
            ManualLogger.Info($"[GetFileMetadataAsync] Cache has {_fileMetadataCache.Count} entries");

            // 🆕 FIX: For zero-knowledge sharing, find metadata entry for the requesting user
            // First, try to find by fileId AND RecipientPubKey match
            string normalizedUserKey;
            try 
            { 
                normalizedUserKey = PublicKeyUtils.NormalizePublicKey(userPubKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not normalize user key: {ex.Message}");
                normalizedUserKey = userPubKey;
            }

            var matchingMetadata = _fileMetadataCache.Values
                .Where(m => {
                    bool fileIdMatch = m.FileId == fileId;
                    if (!fileIdMatch) return false;

                    // 🆕 FIX: For public posts, RecipientPubKey might be empty
                    // If it's a public post (IsPublic = true or EncryptedType = "public"), accept it
                    if (m.IsPublic || string.Equals(m.EncryptedType, "public", StringComparison.OrdinalIgnoreCase))
                    {
                        // Public posts are accessible to everyone, so return it
                        return true;
                    }

                    if (!string.IsNullOrEmpty(m.RecipientPubKey))
                    {
                        try
                        {
                            return PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedUserKey);
                        }
                        catch
                        {
                            return m.RecipientPubKey == normalizedUserKey || m.RecipientPubKey == userPubKey;
                        }
                    }
                    return string.IsNullOrEmpty(m.RecipientPubKey); // Backward compatibility for old files
                })
                .FirstOrDefault();

            if (matchingMetadata != null)
            {
                var recipientKeyPreview = !string.IsNullOrEmpty(matchingMetadata.RecipientPubKey) 
                    ? matchingMetadata.RecipientPubKey.Substring(0, Math.Min(20, matchingMetadata.RecipientPubKey.Length)) 
                    : "(null)";
                Log.Information($"[GetFileMetadataAsync] ✅ Found matching metadata entry for user: FileId={matchingMetadata.FileId}, RecipientPubKey={recipientKeyPreview}...");
                ManualLogger.Info($"[GetFileMetadataAsync] ✅ Found matching metadata entry for user: FileId={matchingMetadata.FileId}, RecipientPubKey={recipientKeyPreview}...");
                return matchingMetadata;
            }
            
            Log.Information($"[GetFileMetadataAsync] ⚠️ No direct match found for fileId={fileId}, trying fallbacks...");
            ManualLogger.Warning($"[GetFileMetadataAsync] ⚠️ No direct match found for fileId={fileId}, trying fallbacks...");

            // 🆕 Fallback 1: Try to find metadata entry by ownerPubKey (for zero-knowledge sharing)
            // When a file is shared with multiple users, they have different fileIds but same ownerPubKey
            FileMetadata? originalMetadata = null;
            
            // Try to load original metadata from cache or disk
            if (!_fileMetadataCache.TryGetValue(fileId, out originalMetadata))
            {
                var metadataPath = Path.Combine(_fileMetadataPath, $"{fileId}.metadata.json");
                if (File.Exists(metadataPath))
                {
                    var json = await File.ReadAllTextAsync(metadataPath);
                    // 🆕 FIX: Use case-insensitive deserialization to handle camelCase JSON
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    originalMetadata = JsonSerializer.Deserialize<FileMetadata>(json, options);
                    if (originalMetadata != null && !string.IsNullOrEmpty(originalMetadata.FileId))
                    {
                        _fileMetadataCache[fileId] = originalMetadata;
                        var ownerKeyPreview = !string.IsNullOrEmpty(originalMetadata.OwnerPubKey) 
                            ? originalMetadata.OwnerPubKey.Substring(0, Math.Min(10, originalMetadata.OwnerPubKey.Length)) 
                            : "(null)";
                        Log.Information($"[GetFileMetadataAsync] Loaded original metadata from disk: FileId={originalMetadata.FileId}, OwnerPubKey={ownerKeyPreview}..., ParentFileId={originalMetadata.ParentFileId ?? "(null)"}");
                    }
                    else
                    {
                        Log.Warning($"[GetFileMetadataAsync] ⚠️ Failed to deserialize metadata from disk: FileId={fileId}, JSON length={json.Length}");
                    }
                }
            }

            // 🆕 FIX: If original metadata found, try to find user's entry by ownerPubKey
            // This works for zero-knowledge sharing where each user has their own fileId
            if (originalMetadata != null && !string.IsNullOrEmpty(originalMetadata.OwnerPubKey))
            {
                Log.Information($"[GetFileMetadataAsync] 🔍 Searching for user's metadata entry with same ownerPubKey: {originalMetadata.OwnerPubKey.Substring(0, Math.Min(10, originalMetadata.OwnerPubKey.Length))}...");
                Log.Information($"[GetFileMetadataAsync] 🔍 Cache has {_fileMetadataCache.Count} entries");
                
                // 🆕 FIX: Reload cache to ensure we have the latest metadata entries
                // This is important because metadata entries might have been uploaded recently
                LoadMetadata();
                Log.Information($"[GetFileMetadataAsync] 🔍 After reload, cache has {_fileMetadataCache.Count} entries");
                
                // Debug: Log all entries with matching ownerPubKey
                var candidates = _fileMetadataCache.Values
                    .Where(m => {
                        if (string.IsNullOrEmpty(m.OwnerPubKey))
                            return false;
                        try
                        {
                            return PublicKeyUtils.AreKeysEqual(m.OwnerPubKey, originalMetadata.OwnerPubKey);
                        }
                        catch
                        {
                            return m.OwnerPubKey == originalMetadata.OwnerPubKey;
                        }
                    })
                    .ToList();
                
                Log.Information($"[GetFileMetadataAsync] 🔍 Found {candidates.Count} entries with matching ownerPubKey");
                foreach (var candidate in candidates.Take(10))
                {
                    var ownerKeyPreview = !string.IsNullOrEmpty(candidate.OwnerPubKey) 
                        ? candidate.OwnerPubKey.Substring(0, Math.Min(10, candidate.OwnerPubKey.Length)) 
                        : "(null)";
                    var recipientKeyPreview = !string.IsNullOrEmpty(candidate.RecipientPubKey) 
                        ? candidate.RecipientPubKey.Substring(0, Math.Min(20, candidate.RecipientPubKey.Length)) 
                        : "(null)";
                    Log.Information($"[GetFileMetadataAsync]   - FileId={candidate.FileId}, RecipientPubKey={recipientKeyPreview}..., Type={candidate.Type ?? "(null)"}, OwnerPubKey={ownerKeyPreview}...");
                }
                
                // Debug: Log user's normalized key for comparison
                Log.Information($"[GetFileMetadataAsync] 🔍 Looking for RecipientPubKey matching: {normalizedUserKey.Substring(0, Math.Min(20, normalizedUserKey.Length))}...");
                
                // 🆕 FIX: Prioritize entries with matching Type and ParentFileId (if originalMetadata has them)
                // This ensures we return the correct metadata entry (e.g., comment vs file image)
                var sameOwnerMetadata = candidates
                    .Where(m => {
                        // Must match the requesting user's RecipientPubKey
                        if (string.IsNullOrEmpty(m.RecipientPubKey))
                            return false;

                        try
                        {
                            if (!PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedUserKey))
                                return false;
                        }
                        catch
                        {
                            if (m.RecipientPubKey != normalizedUserKey && m.RecipientPubKey != userPubKey)
                                return false;
                        }

                        // 🆕 Prioritize: Match Type if originalMetadata has a Type
                        if (!string.IsNullOrEmpty(originalMetadata.Type))
                        {
                            if (!string.Equals(m.Type, originalMetadata.Type, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }

                        // 🆕 Prioritize: Match ParentFileId if originalMetadata has a ParentFileId
                        if (!string.IsNullOrEmpty(originalMetadata.ParentFileId))
                        {
                            if (!string.Equals(m.ParentFileId, originalMetadata.ParentFileId, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }

                        return true;
                    })
                    .FirstOrDefault();

                if (sameOwnerMetadata != null)
                {
                    var recipientKeyPreview = !string.IsNullOrEmpty(sameOwnerMetadata.RecipientPubKey) 
                        ? sameOwnerMetadata.RecipientPubKey.Substring(0, Math.Min(20, sameOwnerMetadata.RecipientPubKey.Length)) 
                        : "(null)";
                    var typePreview = sameOwnerMetadata.Type ?? "(null)";
                    var parentFileIdPreview = !string.IsNullOrEmpty(sameOwnerMetadata.ParentFileId) 
                        ? sameOwnerMetadata.ParentFileId.Substring(0, Math.Min(20, sameOwnerMetadata.ParentFileId.Length)) 
                        : "(null)";
                    Log.Information($"[GetFileMetadataAsync] ✅ Found metadata entry with same ownerPubKey for user: FileId={sameOwnerMetadata.FileId}, Type={typePreview}, ParentFileId={parentFileIdPreview}..., RecipientPubKey={recipientKeyPreview}...");
                    ManualLogger.Info($"[GetFileMetadataAsync] ✅ Found metadata entry with same ownerPubKey for user: FileId={sameOwnerMetadata.FileId}, Type={typePreview}, ParentFileId={parentFileIdPreview}..., RecipientPubKey={recipientKeyPreview}...");
                    return sameOwnerMetadata;
                }
                else
                {
                    Log.Warning($"[GetFileMetadataAsync] ⚠️ No metadata entry found with matching ownerPubKey AND RecipientPubKey={normalizedUserKey.Substring(0, Math.Min(10, normalizedUserKey.Length))}...");
                    Log.Warning($"[GetFileMetadataAsync] ⚠️ User key: {normalizedUserKey.Substring(0, Math.Min(20, normalizedUserKey.Length))}...");
                }
            }

            // 🆕 Fallback 2: If not found by firstBlockId, try to find a related file by ParentFileId
            // This is for cases like comment media where the fileId in relatedFiles might be the owner's
            // but the current user needs their specific metadata entry.
            Log.Information($"[GetFileMetadataAsync] ⚠️ No match found by ownerPubKey, searching for related files by ParentFileId={fileId}...");
            
            var relatedFileMetadata = _fileMetadataCache.Values
                .Where(m => {
                    // Check if this file is related to the requested fileId
                    bool isRelated = !string.IsNullOrEmpty(m.ParentFileId) && m.ParentFileId == fileId;
                    if (!isRelated) return false;

                    // Must match the requesting user's RecipientPubKey
                    if (!string.IsNullOrEmpty(m.RecipientPubKey))
                    {
                        try
                        {
                            return PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedUserKey);
                        }
                        catch
                        {
                            return m.RecipientPubKey == normalizedUserKey || m.RecipientPubKey == userPubKey;
                        }
                    }
                    return false; // Related files should always have a RecipientPubKey
                })
                .FirstOrDefault();

            if (relatedFileMetadata != null)
            {
                var recipientKeyPreview = !string.IsNullOrEmpty(relatedFileMetadata.RecipientPubKey) 
                    ? relatedFileMetadata.RecipientPubKey.Substring(0, Math.Min(20, relatedFileMetadata.RecipientPubKey.Length)) 
                    : "(null)";
                Log.Information($"[GetFileMetadataAsync] ✅ Found related metadata entry for user: FileId={relatedFileMetadata.FileId}, ParentFileId={relatedFileMetadata.ParentFileId}, RecipientPubKey={recipientKeyPreview}...");
                return relatedFileMetadata;
            }
            
            Log.Information($"[GetFileMetadataAsync] ⚠️ No related file found by ParentFileId, trying to load from disk...");

            // 🆕 Final fallback: Try loading from disk (for backward compatibility)
            // But still check if RecipientPubKey matches
            var finalMetadataPath = Path.Combine(_fileMetadataPath, $"{fileId}.metadata.json");
            if (File.Exists(finalMetadataPath))
            {
                var json = await File.ReadAllTextAsync(finalMetadataPath);
                // 🆕 FIX: Use case-insensitive deserialization to handle camelCase JSON
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var metadata = JsonSerializer.Deserialize<FileMetadata>(json, options);
                if (metadata != null && !string.IsNullOrEmpty(metadata.FileId))
                {
                    var recipientKeyPreview = !string.IsNullOrEmpty(metadata.RecipientPubKey) 
                        ? metadata.RecipientPubKey.Substring(0, Math.Min(20, metadata.RecipientPubKey.Length)) 
                        : "(null)";
                    var ownerKeyPreview = !string.IsNullOrEmpty(metadata.OwnerPubKey) 
                        ? metadata.OwnerPubKey.Substring(0, Math.Min(20, metadata.OwnerPubKey.Length)) 
                        : "(null)";
                    Log.Information($"[GetFileMetadataAsync] 🔍 Loaded metadata from disk: FileId={metadata.FileId}, RecipientPubKey={recipientKeyPreview}..., OwnerPubKey={ownerKeyPreview}..., ParentFileId={metadata.ParentFileId ?? "(null)"}");
                    
                    // 🆕 FIX: For public posts, accept them regardless of RecipientPubKey
                    if (metadata.IsPublic || string.Equals(metadata.EncryptedType, "public", StringComparison.OrdinalIgnoreCase))
                    {
                        _fileMetadataCache[fileId] = metadata;
                        Console.WriteLine($"✅ Loaded public metadata from disk: FileId={metadata.FileId}, Type={metadata.Type}");
                        return metadata;
                    }

                    // Check if this metadata entry belongs to the requesting user
                    if (!string.IsNullOrEmpty(metadata.RecipientPubKey))
                    {
                        try
                        {
                            if (PublicKeyUtils.AreKeysEqual(metadata.RecipientPubKey, normalizedUserKey))
                            {
                                _fileMetadataCache[fileId] = metadata;
                                Console.WriteLine($"✅ Loaded matching metadata from disk: FileId={metadata.FileId}, RecipientPubKey={metadata.RecipientPubKey}");
                                return metadata;
                            }
                            else
                            {
                                Console.WriteLine($"⚠️ Metadata RecipientPubKey ({metadata.RecipientPubKey}) does not match user ({normalizedUserKey})");
                                
                                // 🆕 FIX: If RecipientPubKey doesn't match and OwnerPubKey is null,
                                // try to find user's metadata entry by ParentFileId (for comment media)
                                if (string.IsNullOrEmpty(metadata.OwnerPubKey) && !string.IsNullOrEmpty(metadata.ParentFileId))
                                {
                                    Console.WriteLine($"🔍 Metadata has ParentFileId but OwnerPubKey is null, searching for user's entry by ParentFileId...");
                                    LoadMetadata(); // Reload cache to ensure we have all entries
                                    
                                    var userMetadataByParent = _fileMetadataCache.Values
                                        .Where(m => {
                                            // Must have same ParentFileId
                                            if (string.IsNullOrEmpty(m.ParentFileId) || m.ParentFileId != metadata.ParentFileId)
                                                return false;
                                            
                                            // Must match the requesting user's RecipientPubKey
                                            if (string.IsNullOrEmpty(m.RecipientPubKey))
                                                return false;
                                            
                                            try
                                            {
                                                return PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedUserKey);
                                            }
                                            catch
                                            {
                                                return m.RecipientPubKey == normalizedUserKey || m.RecipientPubKey == userPubKey;
                                            }
                                        })
                                        .FirstOrDefault();
                                    
                                    if (userMetadataByParent != null)
                                    {
                                        Console.WriteLine($"✅ Found user's metadata entry by ParentFileId: FileId={userMetadataByParent.FileId}, ParentFileId={userMetadataByParent.ParentFileId}, RecipientPubKey={userMetadataByParent.RecipientPubKey}");
                                        return userMetadataByParent;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"⚠️ No metadata entry found with ParentFileId={metadata.ParentFileId} and RecipientPubKey matching user");
                                    }
                                }
                            }
                        }
                        catch
                        {
                            if (metadata.RecipientPubKey == normalizedUserKey || metadata.RecipientPubKey == userPubKey)
                            {
                                _fileMetadataCache[fileId] = metadata;
                                Console.WriteLine($"✅ Loaded matching metadata from disk (direct): FileId={metadata.FileId}");
                                return metadata;
                            }
                            else
                            {
                                Console.WriteLine($"⚠️ Metadata RecipientPubKey ({metadata.RecipientPubKey}) does not match user (direct comparison)");
                                
                                // 🆕 FIX: Same logic for catch block
                                if (string.IsNullOrEmpty(metadata.OwnerPubKey) && !string.IsNullOrEmpty(metadata.ParentFileId))
                                {
                                    Console.WriteLine($"🔍 Metadata has ParentFileId but OwnerPubKey is null, searching for user's entry by ParentFileId...");
                                    LoadMetadata();
                                    
                                    var userMetadataByParent = _fileMetadataCache.Values
                                        .Where(m => {
                                            if (string.IsNullOrEmpty(m.ParentFileId) || m.ParentFileId != metadata.ParentFileId)
                                                return false;
                                            if (string.IsNullOrEmpty(m.RecipientPubKey))
                                                return false;
                                            return m.RecipientPubKey == normalizedUserKey || m.RecipientPubKey == userPubKey;
                                        })
                                        .FirstOrDefault();
                                    
                                    if (userMetadataByParent != null)
                                    {
                                        Console.WriteLine($"✅ Found user's metadata entry by ParentFileId (catch): FileId={userMetadataByParent.FileId}");
                                        return userMetadataByParent;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // 🆕 If no RecipientPubKey but has OwnerPubKey, try to find user's entry by OwnerPubKey
                        if (!string.IsNullOrEmpty(metadata.OwnerPubKey))
                        {
                            Console.WriteLine($"🔍 Metadata has no RecipientPubKey but has OwnerPubKey, searching cache for user's entry...");
                            var userMetadata = _fileMetadataCache.Values
                                .Where(m => {
                                    if (string.IsNullOrEmpty(m.OwnerPubKey) || string.IsNullOrEmpty(m.RecipientPubKey))
                                        return false;
                                    
                                    try
                                    {
                                        bool ownerMatch = PublicKeyUtils.AreKeysEqual(m.OwnerPubKey, metadata.OwnerPubKey);
                                        bool recipientMatch = PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedUserKey);
                                        return ownerMatch && recipientMatch;
                                    }
                                    catch
                                    {
                                        return m.OwnerPubKey == metadata.OwnerPubKey && 
                                               (m.RecipientPubKey == normalizedUserKey || m.RecipientPubKey == userPubKey);
                                    }
                                })
                                .FirstOrDefault();
                            
                            if (userMetadata != null)
                            {
                                Console.WriteLine($"✅ Found user's metadata entry by OwnerPubKey: FileId={userMetadata.FileId}, RecipientPubKey={userMetadata.RecipientPubKey}");
                                return userMetadata;
                            }
                        }
                        
                        // Backward compatibility: if no RecipientPubKey and no OwnerPubKey match, don't return it
                        Console.WriteLine($"⚠️ Loaded metadata without RecipientPubKey but no matching user entry found. FileId={metadata.FileId}");
                        // Don't return - this will throw FileNotFoundException below
                    }
                }
            }

            // If we get here, no matching metadata was found
            Log.Warning($"[GetFileMetadataAsync] ❌ File metadata not found: {fileId} for user {userPreview}...");
            ManualLogger.Error($"[GetFileMetadataAsync] ❌ File metadata not found: {fileId} for user {userPreview}...");
            throw new FileNotFoundException($"File metadata not found: {fileId} (or access denied for user)");
        }

        public async Task<List<FileMetadata>> ListUserFilesAsync(string pubKey)
        {
            EnsureStorageDirectories();
            LoadMetadata(); // Reload metadata to catch any new files

            Console.WriteLine($"FileStorage: Looking for files for user: {pubKey} ({pubKey.Length} chars)");
            
            // 🆕 NORMALIZE: Normalize the search key for consistent comparison
            string normalizedSearchKey;
            try
            {
                normalizedSearchKey = PublicKeyUtils.NormalizePublicKey(pubKey);
                Console.WriteLine($"FileStorage: Normalized search key: {normalizedSearchKey}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FileStorage: Warning - could not normalize search key: {ex.Message}");
                normalizedSearchKey = pubKey; // Fallback to original key
            }

            var result = _fileMetadataCache.Values
                .Where(m => {
                    try
                    {
                        // 🆕 IMPROVED: Use PublicKeyUtils to compare keys (handles both formats)
                        bool isMatch = PublicKeyUtils.AreKeysEqual(m.RecipientPubKey, normalizedSearchKey);
                        if (isMatch)
                        {
                            Console.WriteLine($"FileStorage: ✅ Found matching file {m.FileId} for normalized key {normalizedSearchKey}");
                        }
                        return isMatch;
                    }
                    catch
                    {
                        // Fallback to direct string comparison if normalization fails
                        bool directMatch = m.RecipientPubKey == pubKey;
                        if (directMatch)
                        {
                            Console.WriteLine($"FileStorage: ✅ Found matching file {m.FileId} via direct comparison");
                        }
                        return directMatch;
                    }
                })
                .ToList();
                
            Console.WriteLine($"FileStorage: Found {result.Count} files for user {pubKey}");
            foreach (var file in result)
            {
                Console.WriteLine($"  - File {file.FileId}: RecipientPubKey={file.RecipientPubKey}, Type={file.Type}");
            }
            
            return await Task.FromResult(result);
        }

        public async Task<bool> FileExistsAsync(string fileId)
        {
            EnsureStorageDirectories();

            var exists = _fileMetadataCache.ContainsKey(fileId);
            if (!exists)
            {
                var metadataPath = Path.Combine(_fileMetadataPath, $"{fileId}.metadata.json");
                exists = File.Exists(metadataPath);
            }
            return await Task.FromResult(exists);
        }

        private string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".txt": return "text/plain";
                case ".pdf": return "application/pdf";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".doc": return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                default: return "application/octet-stream";
            }
        }

        public async Task<List<FileMetadata>> ListAllPostsByShareTypeAsync(string shareType)
        {
            EnsureStorageDirectories();
            LoadMetadata();
            var result = _fileMetadataCache.Values
                .Where(m => m.Type == "post" && string.Equals(m.ShareType, shareType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return await Task.FromResult(result);
        }

        public async Task<List<FileMetadata>> ListAllPublicPostsAsync()
        {
            EnsureStorageDirectories();
            LoadMetadata();
            Console.WriteLine($"[PUBLIC FEED] Total metadata loaded: {_fileMetadataCache.Count}");
            var result = _fileMetadataCache.Values
                .Where(m => (m.IsPublic || string.Equals(m.EncryptedType, "public", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Console.WriteLine($"[PUBLIC FEED] Matching public entries: {result.Count}");
            foreach (var m in result.Take(5))
            {
                Console.WriteLine($"  - FileId={m.FileId}, Type={m.Type}, IsPublic={m.IsPublic}, EncryptedType={m.EncryptedType}, ShareType={m.ShareType}");
            }
            return await Task.FromResult(result);
        }

        public async Task<List<FileMetadata>> ListReactionsByParentAsync(string parentFileId)
        {
            EnsureStorageDirectories();
            LoadMetadata();
            var result = _fileMetadataCache.Values
                .Where(m => string.Equals(m.Type, "react", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(m.ParentFileId, parentFileId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return await Task.FromResult(result);
        }

        public async Task<List<FileMetadata>> ListCommentsByParentAsync(string parentFileId)
        {
            EnsureStorageDirectories();
            // Reload metadata to get latest comments (including newly uploaded ones)
            // Add a small delay to ensure files are flushed to disk
            await Task.Delay(100); // Wait 100ms for file system to flush
            LoadMetadata();
            
            Console.WriteLine($"[ListCommentsByParentAsync] Looking for comments with parentFileId: {parentFileId}");
            Console.WriteLine($"[ListCommentsByParentAsync] Total metadata entries in cache: {_fileMetadataCache.Count}");
            
            // Debug: List all comments
            var allComments = _fileMetadataCache.Values
                .Where(m => string.Equals(m.Type, "comment", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Console.WriteLine($"[ListCommentsByParentAsync] Found {allComments.Count} total comments in cache");
            foreach (var c in allComments)
            {
                Console.WriteLine($"[ListCommentsByParentAsync] Comment FileId: {c.FileId}, ParentFileId: {c.ParentFileId ?? "(null)"}, Type: {c.Type}, IsPublic: {c.IsPublic}");
            }
            
            // Debug: Check each comment's ParentFileId
            // For encrypted comments, there may be multiple metadata entries (one per recipient)
            // We need to deduplicate by FileId to avoid returning duplicates
            var seenFileIds = new HashSet<string>();
            var matchingComments = new List<FileMetadata>();
            foreach (var m in _fileMetadataCache.Values)
            {
                if (string.Equals(m.Type, "comment", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip duplicates (encrypted comments have multiple metadata entries)
                    if (seenFileIds.Contains(m.FileId))
                    {
                        Console.WriteLine($"[ListCommentsByParentAsync] Skipping duplicate FileId: {m.FileId}");
                        continue;
                    }
                    
                    var parentMatch = !string.IsNullOrEmpty(m.ParentFileId) && 
                                     string.Equals(m.ParentFileId, parentFileId, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"[ListCommentsByParentAsync] Comment {m.FileId}: ParentFileId='{m.ParentFileId ?? "(null)"}', Looking for='{parentFileId}', Match={parentMatch}");
                    if (parentMatch)
                    {
                        seenFileIds.Add(m.FileId);
                        matchingComments.Add(m);
                    }
                }
            }
            
            var result = matchingComments
                .OrderBy(m => m.CreationTime) // Sort by creation time (oldest first)
                .ToList();
            
            Console.WriteLine($"[ListCommentsByParentAsync] Found {result.Count} unique comments matching parentFileId {parentFileId}");
            return await Task.FromResult(result);
        }

        public async Task<bool> DeleteFileByFileIdAsync(string fileId)
        {
            try
            {
                EnsureStorageDirectories();
                LoadMetadata();

                if (!_fileMetadataCache.TryGetValue(fileId, out var metadata))
                {
                    var metaPath = Path.Combine(_fileMetadataPath, $"{fileId}.metadata.json");
                    if (!File.Exists(metaPath)) return false;
                    var json = await File.ReadAllTextAsync(metaPath);
                    metadata = JsonSerializer.Deserialize<FileMetadata>(json);
                }

                if (metadata == null) return false;

                // Delete file metadata
                var path = Path.Combine(_fileMetadataPath, $"{fileId}.metadata.json");
                if (File.Exists(path)) File.Delete(path);
                _fileMetadataCache.Remove(fileId);

                // Best-effort: delete block 0 (metadata block) if present
                if (!string.IsNullOrEmpty(metadata.FirstBlockId))
                {
                    var bData = Path.Combine(_blockDataPath, $"{metadata.FirstBlockId}.data");
                    var bMeta = Path.Combine(_blockMetadataPath, $"{metadata.FirstBlockId}.metadata.json");
                    if (File.Exists(bData)) File.Delete(bData);
                    if (File.Exists(bMeta)) File.Delete(bMeta);
                    _blockMetadataCache.Remove(metadata.FirstBlockId);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DeleteFileByFileIdAsync error: {ex.Message}");
                return false;
            }
        }
    }
} 