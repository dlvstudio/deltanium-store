using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using DeltaniumStore.Services;
using DeltaniumStore.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Linq;
using Serilog;

namespace DeltaniumStore.Controllers
{
    /// <summary>
    /// Controller for managing file operations in Deltanium Store
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class FileController : ControllerBase
    {
        private readonly IFileStorageService _fileStorage;
        private readonly IStorageContractService _contractService;
        private readonly IBlockchainService _blockchainService;

        public FileController(
            IFileStorageService fileStorage,
            IStorageContractService contractService,
            IBlockchainService blockchainService)
        {
            _fileStorage = fileStorage;
            _contractService = contractService;
            _blockchainService = blockchainService;
        }

        /// <summary>
        /// Upload a file to the store
        /// </summary>
        /// <param name="file">The file to upload</param>
        /// <returns>Metadata of the uploaded file</returns>
        /// <response code="200">Returns the metadata of the uploaded file</response>
        /// <response code="400">If the file is null or empty</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpPost("upload")]
        [ProducesResponseType(typeof(FileMetadata), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UploadFile([FromForm] IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest("No file uploaded");
                }

                // Get the user's public key from headers
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // Parse the content type from headers
                string contentType = Request.Headers["Content-Type"].ToString().ToLower();
                bool isMetadata = contentType.Contains("application/json") || file.ContentType.Contains("application/json");

                // Check if this is a block file
                bool isBlock = file.FileName.Contains("_block_");

                Console.WriteLine($"Processing file upload: {file.FileName}");
                Console.WriteLine($"Content type: {contentType}");
                Console.WriteLine($"Is metadata: {isMetadata}");
                Console.WriteLine($"Is block: {isBlock}");

                if (isBlock)
                {
                    // Extract blockId and fileId from filename
                    var parts = file.FileName.Split("_block_");
                    if (parts.Length != 2)
                    {
                        return BadRequest("Invalid block filename format");
                    }

                    string fileId = parts[0];
                    string blockId = file.FileName;
                    int blockIndex;
                    if (!int.TryParse(parts[1], out blockIndex))
                    {
                        return BadRequest("Invalid block index format");
                    }

                    // Verify that the file metadata exists before storing block
                    try
                    {
                        await _fileStorage.GetFileMetadataAsync(fileId, userPubKey);
                    }
                    catch (FileNotFoundException)
                    {
                        return BadRequest($"File metadata not found for ID: {fileId}. Please upload metadata first.");
                    }

                    using (var stream = file.OpenReadStream())
                    {
                        var blockMetadata = await _fileStorage.StoreBlockAsync(
                            stream,
                            blockId,
                            blockIndex
                        );

                        return Ok(new
                        {
                            blockMetadata.BlockId,
                            // FileId removed for privacy
                            blockMetadata.BlockSize,
                            blockMetadata.BlockIndex,
                            blockMetadata.CreationTime
                        });
                    }
                }
                else if (isMetadata)
                {
                    // Parse the metadata JSON from the file
                    using (var reader = new StreamReader(file.OpenReadStream()))
                    {
                        var json = await reader.ReadToEndAsync();
                        Console.WriteLine($"Received metadata JSON: {json}");
                        
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };
                        var metadata = JsonSerializer.Deserialize<FileMetadata>(json, options);
                        
                        if (metadata == null)
                        {
                            return BadRequest("Invalid metadata format");
                        }

                        // 🔧 FIX: FileId should be provided by client, not extracted from FirstBlockId
                        // FileId is unique per user, FirstBlockId is unique per user's metadata block
                        if (string.IsNullOrEmpty(metadata.FileId))
                        {
                            return BadRequest("FileId is required in metadata");
                        }
                        
                        if (string.IsNullOrEmpty(metadata.FirstBlockId))
                        {
                            return BadRequest("FirstBlockId is required in metadata");
                        }

                        // Không set OwnerPubKey nữa, chỉ dùng RecipientPubKey
                        
                        Console.WriteLine($"Storing metadata with FileId: {metadata.FileId}");
                        var storedMetadata = await _fileStorage.StoreFileMetadataAsync(metadata);
                        return Ok(new
                        {
                            storedMetadata.FileId,
                            // 🔒 REMOVED sensitive fields for zero-knowledge storage:
                            // storedMetadata.FileName, ❌
                            // storedMetadata.FileSize, ❌
                            // storedMetadata.MimeType, ❌
                            // storedMetadata.FileExtension, ❌
                            storedMetadata.CreationTime,
                            storedMetadata.FirstBlockId, // 🆕 Single first block ID instead of full list
                            storedMetadata.EncryptedKey, // 🆕 Single encrypted key instead of dictionary
                            storedMetadata.Version
                        });
                    }
                }
                else
                {
                    return BadRequest("Invalid file type. Must be either a block file or metadata file.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in upload: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Download a file from the store
        /// </summary>
        /// <param name="fileId">ID of the file to download</param>
        /// <returns>File stream</returns>
        /// <response code="200">Returns the requested file</response>
        /// <response code="400">If the file ID is invalid</response>
        /// <response code="401">If the user doesn't have access to the file</response>
        /// <response code="404">If the file was not found</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("download/{fileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DownloadFile(string fileId)
        {
            try
            {
                // Get the user's public key from headers
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                var userPreview = userPubKey.Substring(0, Math.Min(20, userPubKey.Length));
                Log.Information($"[DownloadFile] Download request for: {fileId}, User: {userPreview}...");
                ManualLogger.Info($"[DownloadFile] Download request for: {fileId}, User: {userPreview}...");

                // This endpoint now only returns file metadata
                // For blocks, use the new dedicated endpoints below
                var metadata = await _fileStorage.GetFileMetadataAsync(fileId, userPubKey);
                var recipientKeyPreview = !string.IsNullOrEmpty(metadata.RecipientPubKey) 
                    ? metadata.RecipientPubKey.Substring(0, Math.Min(20, metadata.RecipientPubKey.Length)) 
                    : "(null)";
                Log.Information($"[DownloadFile] ✅ Found metadata: FileId={metadata.FileId}, Type={metadata.Type ?? "(null)"}, RecipientPubKey={recipientKeyPreview}...");
                ManualLogger.Info($"[DownloadFile] ✅ Found metadata: FileId={metadata.FileId}, Type={metadata.Type ?? "(null)"}, RecipientPubKey={recipientKeyPreview}...");
                return Ok(metadata);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning($"[DownloadFile] Unauthorized access: {ex.Message}");
                return Unauthorized("You don't have access to this file");
            }
            catch (FileNotFoundException ex)
            {
                Log.Warning($"[DownloadFile] File not found: {fileId}, Error: {ex.Message}");
                return NotFound("File not found");
            }
            catch (Exception ex)
            {
                Log.Error($"[DownloadFile] Error in download: {ex.Message}, Stack: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Download a specific block by blockId
        /// </summary>
        /// <param name="blockId">ID of the block to download</param>
        /// <returns>Block data stream</returns>
        /// <response code="200">Returns the requested block</response>
        /// <response code="404">If the block was not found</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("block/{blockId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DownloadBlock(string blockId)
        {
            try
            {
                Console.WriteLine($"Block download request for: {blockId}");
                
                // Download block directly using the random UUID
                var blockStream = await _fileStorage.GetBlockAsync(blockId);
                return File(blockStream, "application/octet-stream", blockId);
            }
            catch (FileNotFoundException)
            {
                return NotFound("Block not found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in block download: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Download a file block by file ID and block index (uses file metadata to get block ID)
        /// </summary>
        /// <param name="fileId">ID of the file</param>
        /// <param name="blockIndex">Index of the block</param>
        /// <returns>Block data stream</returns>
        /// <response code="200">Returns the requested block</response>
        /// <response code="404">If the file or block was not found</response>
        /// <response code="401">If the user doesn't have access</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("block/{fileId}/{blockIndex}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DownloadBlockByIndex(string fileId, int blockIndex)
        {
            try
            {
                // Get the user's public key from headers
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }
                
                Console.WriteLine($"Block download request for file: {fileId}, block: {blockIndex}");
                
                // 🆕 ZERO-KNOWLEDGE SHARING: Block structure is now in encrypted metadata
                // For block index 0, use FirstBlockId directly
                if (blockIndex == 0)
                {
                    var metadata = await _fileStorage.GetFileMetadataAsync(fileId, userPubKey);
                    var blockId = metadata.FirstBlockId;
                    Console.WriteLine($"Resolved block index 0 to firstBlockId: {blockId}");
                    
                    // Download block using the first block ID
                    var blockStream = await _fileStorage.GetBlockAsync(blockId);
                    return File(blockStream, "application/octet-stream", blockId);
                }
                else
                {
                    // For content blocks (index > 0), we need to decrypt metadata block first
                    // This is more complex and should be handled by the client
                    return BadRequest("Content block access requires decrypting metadata block first. Use direct block ID endpoint instead.");
                }

            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized("You don't have access to this file");
            }
            catch (FileNotFoundException)
            {
                return NotFound("File or block not found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in block download: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// List all files accessible by the user
        /// </summary>
        /// <returns>List of file metadata</returns>
        /// <response code="200">Returns the list of files</response>
        /// <response code="400">If the user public key is missing</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("list")]
        [ProducesResponseType(typeof(List<FileMetadata>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ListFiles()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                var files = await _fileStorage.ListUserFilesAsync(userPubKey);
                return Ok(files);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a file exists in the store
        /// </summary>
        /// <param name="fileId">ID of the file to check</param>
        /// <returns>Boolean indicating if the file exists</returns>
        /// <response code="200">Returns whether the file exists</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("exists/{fileId}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CheckFileExists(string fileId)
        {
            try
            {
                var exists = await _fileStorage.FileExistsAsync(fileId);
                return Ok(new { exists });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Upload metadata raw - Accepts raw JSON body instead of multipart
        /// </summary>
        /// <returns>Result of the operation</returns>
        /// <response code="200">Returns the result of the operation</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="401">If the user is unauthorized</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpPost("upload-metadata-raw")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UploadMetadataRawAsync()
        {
            try
            {
                // Get the user's public key from headers
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // Đọc raw body (application/json) từ Request.Body
                using (var sr = new StreamReader(Request.Body, System.Text.Encoding.UTF8))
                {
                    var json = await sr.ReadToEndAsync();
                    Console.WriteLine($"[Raw API] Received metadata JSON: {json}");
                    
                    // Parse JSON vào đối tượng FileMetadata
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var metadata = JsonSerializer.Deserialize<FileMetadata>(json, options);
                    
                    if (metadata == null)
                    {
                        return BadRequest("Invalid metadata format");
                    }

                    // 🔧 FIX: FileId must be provided by client (unique per user)
                    // FirstBlockId is also provided by client (unique per user's metadata block)
                    if (string.IsNullOrEmpty(metadata.FileId))
                    {
                        return BadRequest("FileId is required in metadata");
                    }
                    
                    if (string.IsNullOrEmpty(metadata.FirstBlockId))
                    {
                        return BadRequest("FirstBlockId is required in metadata");
                    }

                    // Ensure PRE fields normalization (optional)
                    if (!string.IsNullOrEmpty(metadata.PolicyTag))
                    {
                        metadata.PolicyTag = metadata.PolicyTag.Trim();
                    }
                    if (!string.IsNullOrEmpty(metadata.CapsuleFor))
                    {
                        metadata.CapsuleFor = metadata.CapsuleFor.Trim().ToLower();
                    }
                    if (!string.IsNullOrEmpty(metadata.PolicyScheme))
                    {
                        metadata.PolicyScheme = metadata.PolicyScheme.Trim().ToUpper();
                    }

                    // 🆕 For reaction uploads: ensure OwnerPubKey is set to caller if missing
                    try
                    {
                        if (string.Equals(metadata.Type, "react", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(metadata.OwnerPubKey))
                            {
                                metadata.OwnerPubKey = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(userPubKey);
                            }
                        }
                    }
                    catch { metadata.OwnerPubKey = userPubKey; }

                    // Ensure public flags for public posts
                    try
                    {
                        bool looksPublic = (metadata.EncryptedKey == null || metadata.EncryptedKey.Length == 0) ||
                                           (string.Equals(metadata.EncryptedType, "public", StringComparison.OrdinalIgnoreCase));
                        if (looksPublic)
                        {
                            metadata.IsPublic = true;
                            if (string.IsNullOrEmpty(metadata.EncryptedType))
                            {
                                metadata.EncryptedType = "public";
                            }
                        }
                        Console.WriteLine($"[Raw API] Upload metadata flags: Type={metadata.Type}, IsPublic={metadata.IsPublic}, EncryptedType={metadata.EncryptedType}, ShareType={metadata.ShareType}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Raw API] Flag normalization failed: {ex.Message}");
                    }

                    // 🔒 PRIORITY 1 SECURITY FIX: Validate comment parentFileId
                    if (string.Equals(metadata.Type, "comment", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(metadata.ParentFileId))
                        {
                            return BadRequest("ParentFileId is required for comments");
                        }

                        // Validate parent post exists and user has access
                        try
                        {
                            var parentPost = await _fileStorage.GetFileMetadataAsync(metadata.ParentFileId, userPubKey);
                            if (parentPost == null)
                            {
                                return BadRequest("Parent post not found or access denied");
                            }

                            // 🆕 DEBUG: Log parent post metadata for troubleshooting
                            Console.WriteLine($"[Comment Validation] Parent post metadata: FileId={parentPost.FileId}, Type={parentPost.Type ?? "(null)"}, IsPublic={parentPost.IsPublic}, EncryptedType={parentPost.EncryptedType}");

                            // Validate that parent file is actually a post
                            if (string.IsNullOrEmpty(parentPost.Type) || 
                                !string.Equals(parentPost.Type, "post", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[Comment Validation] ERROR: Parent file Type is '{parentPost.Type ?? "(null)"}', expected 'post'");
                                return BadRequest("Parent file is not a post. Comments can only be created for posts.");
                            }

                            // 🔒 PRIORITY 1 SECURITY FIX: Validate postOwnerPublicKey for encrypted comments
                            // For encrypted comments (shareType == "author"), validate that RecipientPubKey matches actual post owner
                            if (!string.IsNullOrEmpty(metadata.ShareType) && 
                                string.Equals(metadata.ShareType, "author", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(metadata.RecipientPubKey))
                            {
                                // Get the actual owner of the parent post
                                // For posts, OwnerPubKey should be set, but if not, we can't validate
                                if (string.IsNullOrEmpty(parentPost.OwnerPubKey))
                                {
                                    Console.WriteLine($"[Raw API] SECURITY WARNING: Parent post has no OwnerPubKey, cannot validate postOwnerPublicKey");
                                    // For backward compatibility, we'll allow this but log a warning
                                }
                                else
                                {
                                    var normalizedParentOwner = string.Empty;
                                    var normalizedRecipient = string.Empty;
                                    try
                                    {
                                        normalizedParentOwner = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(parentPost.OwnerPubKey);
                                        normalizedRecipient = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(metadata.RecipientPubKey);
                                    }
                                    catch
                                    {
                                        normalizedParentOwner = parentPost.OwnerPubKey;
                                        normalizedRecipient = metadata.RecipientPubKey;
                                    }

                                    // Validate that RecipientPubKey matches the actual post owner
                                    if (!DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(normalizedParentOwner, normalizedRecipient))
                                    {
                                        Console.WriteLine($"[Raw API] SECURITY: Invalid postOwnerPublicKey. Expected: {normalizedParentOwner.Substring(0, Math.Min(10, normalizedParentOwner.Length))}..., Got: {normalizedRecipient.Substring(0, Math.Min(10, normalizedRecipient.Length))}...");
                                        return BadRequest("Invalid post owner public key. RecipientPubKey does not match the actual post owner.");
                                    }
                                }
                            }
                        }
                        catch (FileNotFoundException)
                        {
                            return BadRequest("Parent post not found or access denied");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Raw API] Error validating parent post: {ex.Message}");
                            return BadRequest("Parent post not found or access denied");
                        }
                    }

                    // Không set OwnerPubKey nữa, chỉ dùng RecipientPubKey
                    
                    Log.Information($"[Raw API] Storing metadata with FileId: {metadata.FileId}");
                    var storedMetadata = await _fileStorage.StoreFileMetadataAsync(metadata);

                    Log.Information($"[Raw API] Before TrySubmitStorageContractAsync for contract: {metadata.ContractId}");
                    var submitResult = await TrySubmitStorageContractAsync(metadata, userPubKey);
                    Log.Information($"[Raw API] After TrySubmitStorageContractAsync for contract: {metadata.ContractId}");

                    return Ok(new
                    {
                        storedMetadata.FileId,
                        // 🔒 REMOVED sensitive fields for zero-knowledge storage:
                        // storedMetadata.FileName, ❌ -> Now in encrypted metadata block
                        // storedMetadata.FileSize, ❌ -> Now in encrypted metadata block
                        // storedMetadata.MimeType, ❌ -> Now in encrypted metadata block
                        // storedMetadata.FileExtension, ❌ -> Now in encrypted metadata block
                                                    storedMetadata.CreationTime,
                            storedMetadata.FirstBlockId, // 🆕 Single first block ID instead of full list
                            storedMetadata.EncryptedKey, // 🆕 Single encrypted key instead of dictionary
                        storedMetadata.Version,
                        BlockchainSubmitted = submitResult.Success,
                        BlockchainSubmitError = submitResult.Success ? null : submitResult.ErrorMessage,
                        Message = "Metadata uploaded (raw) successfully."
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raw API] Error in metadata upload: {ex.Message}");
                Console.WriteLine($"[Raw API] Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Upload file block raw - Accepts raw binary body instead of multipart
        /// </summary>
        /// <returns>Result of the operation</returns>
        /// <response code="200">Returns the result of the operation</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="401">If the user is unauthorized</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpPost("upload-file-block-raw")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UploadFileBlockRawAsync()
        {
            try
            {
                // Get the user's public key from headers
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // We need blockId and fileId in headers since they're not part of the multipart anymore
                var blockId = Request.Headers["X-Block-Id"].ToString();
                if (string.IsNullOrEmpty(blockId))
                {
                    return BadRequest("Block ID is required in X-Block-Id header");
                }

                var fileId = Request.Headers["X-File-Id"].ToString();
                if (string.IsNullOrEmpty(fileId))
                {
                    return BadRequest("File ID is required in X-File-Id header");
                }

                int blockIndex;
                var blockIndexHeader = Request.Headers["X-Block-Index"].ToString();
                if (string.IsNullOrEmpty(blockIndexHeader) || !int.TryParse(blockIndexHeader, out blockIndex))
                {
                    return BadRequest("Valid Block Index is required in X-Block-Index header");
                }
                
                Console.WriteLine($"[Raw API] Processing block upload: {blockId}");
                Console.WriteLine($"[Raw API] File ID: {fileId}, Block Index: {blockIndex}");

                // Verify that the file metadata exists before storing block
                try
                {
                    await _fileStorage.GetFileMetadataAsync(fileId, userPubKey);
                }
                catch (FileNotFoundException)
                {
                    return BadRequest($"File metadata not found for ID: {fileId}. Please upload metadata first.");
                }

                // Read the raw body as a stream
                using (var ms = new MemoryStream())
                {
                    await Request.Body.CopyToAsync(ms);
                    ms.Position = 0; // Reset position to the beginning

                    Console.WriteLine($"[Raw API] Received {ms.Length} bytes for block");
                    
                    var blockMetadata = await _fileStorage.StoreBlockAsync(
                        ms,
                        blockId,
                        blockIndex
                    );

                    return Ok(new
                    {
                        blockMetadata.BlockId,
                        // FileId removed for privacy
                        blockMetadata.BlockSize,
                        blockMetadata.BlockIndex,
                        blockMetadata.CreationTime,
                        Message = "File block uploaded (raw) successfully."
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raw API] Error in block upload: {ex.Message}");
                Console.WriteLine($"[Raw API] Stack trace: {ex.StackTrace}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        private async Task<SubmitResult> TrySubmitStorageContractAsync(FileMetadata metadata, string userPubKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(metadata.ContractId))
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: No ContractId. Skipping submission.");
                    return new SubmitResult(false, "No ContractId");
                }

                if (!string.Equals(metadata.Type, "post", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(metadata.Type, "react", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(metadata.Type, "comment", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(metadata.Type, "chat_message", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: Metadata type is '{metadata.Type}'. Not a post/react/comment/chat. Skipping submission.");
                    return new SubmitResult(false, "Not a post/react/comment/chat_message");
                }

                var contract = await _contractService.GetContractAsync(metadata.ContractId);
                if (contract == null)
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: Contract {metadata.ContractId} not found. Skipping submission.");
                    return new SubmitResult(false, "Contract not found");
                }

                if (contract.BlockchainSubmittedAtUnix.HasValue && contract.BlockchainSubmittedAtUnix.Value > 0)
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: Contract {metadata.ContractId} already submitted to blockchain. Skipping submission.");
                    return new SubmitResult(true);
                }

                if (!string.Equals(contract.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: Contract {metadata.ContractId} status is '{contract.Status}', not 'Active'. Skipping submission.");
                    return new SubmitResult(false, $"Contract status {contract.Status}");
                }

                var matchesApp = string.Equals(contract.AppPublicKey, userPubKey, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(contract.AppPublicKey, metadata.OwnerPubKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                if (!matchesApp)
                {
                    Log.Information($"[Raw API] TrySubmitStorageContract: AppPublicKey mismatch for contract {metadata.ContractId}. App: {contract.AppPublicKey}, User: {userPubKey}. Skipping submission.");
                    return new SubmitResult(false, "AppPublicKey mismatch");
                }

                var submitResult = await _blockchainService.SubmitStorageOperationAsync(metadata, contract);
                if (!submitResult.Success)
                {
                    Log.Warning($"[Raw API] TrySubmitStorageContract: SubmitStorageOperationAsync returned false for contract {metadata.ContractId}");
                    return submitResult;
                }

                contract.BlockchainSubmittedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _contractService.UpdateContractAsync(contract);
                Log.Information($"[Raw API] TrySubmitStorageContract: Successfully submitted contract {metadata.ContractId} to blockchain.");
                return new SubmitResult(true);
            }
            catch (Exception ex)
            {
                Log.Error($"[Raw API] StorageOperation submit error: {ex.Message}");
                return new SubmitResult(false, ex.Message);
            }
        }

        /// <summary>
        /// PRE: Re-encrypt an encryptedKey (PRE bundle) for a follower (proxy flow)
        /// </summary>
        [HttpPost("pre/reencrypt")]
        public async Task<IActionResult> ReencryptForFollowerAsync([FromBody] JsonElement body)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString(); // pk_B
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                var fileId = body.TryGetProperty("fileId", out var fid) ? fid.GetString() : null;
                var policyTag = body.TryGetProperty("policyTag", out var tag) ? tag.GetString() : null;
                var token = body.TryGetProperty("token", out var tok) ? tok.GetString() : null;

                if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(policyTag))
                {
                    return BadRequest("fileId and policyTag are required");
                }

                // Load metadata and verify policy fields
                var metadata = await _fileStorage.GetFileMetadataAsync(fileId, userPubKey);
                if (metadata == null)
                {
                    return NotFound("File metadata not found");
                }
                if (!string.Equals(metadata.CapsuleFor, "tag", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("File is not a PRE-tagged entry");
                }
                if (!string.Equals(metadata.PolicyTag, policyTag, StringComparison.Ordinal))
                {
                    return BadRequest("PolicyTag mismatch");
                }

                // Verify token with Central (prototype). Expect rk (base64) if valid
                byte[]? rkBytes = null;
                if (!string.IsNullOrEmpty(token))
                {
                    var central = HttpContext.RequestServices.GetService(typeof(ICentralApiService)) as ICentralApiService;
                    var rkBase64 = central != null ? await central.ValidatePreTokenAsync(token) : null;
                    if (!string.IsNullOrEmpty(rkBase64))
                    {
                        rkBytes = Convert.FromBase64String(rkBase64);
                    }
                }

                // Transform using native PRE library via Deltanium.Core.Cryptography.Pre.UmbralPre
                // metadata.EncryptedKey contains PRE bundle for author (base64 from app). It may be bytes already.
                var inputBundle = metadata.EncryptedKey;
                if (inputBundle == null || inputBundle.Length == 0)
                {
                    return BadRequest("Missing encryptedKey in metadata");
                }

                // If rk available, perform transform via core UmbralPre; otherwise echo bundle (client fallback)
                byte[] outputBundle = inputBundle;
                if (rkBytes != null && rkBytes.Length > 0)
                {
                    try
                    {
                        outputBundle = Deltanium.Core.Cryptography.Pre.UmbralPre.Reencrypt(inputBundle, rkBytes);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PRE] Native reencrypt failed, falling back: {ex.Message}");
                    }
                }
                var encryptedKeyForB = Convert.ToBase64String(outputBundle);
                return Ok(new { encryptedKeyForB });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PRE] Reencrypt error: {ex.Message}");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// List posts only (files with type='post' in metadata)
        /// </summary>
        /// <returns>List of post files</returns>
        /// <response code="200">Returns the list of posts</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("posts")]
        [ProducesResponseType(typeof(List<FileMetadata>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ListPosts()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // 🆕 Now we can filter posts at storage level since type is in metadata
                var files = await _fileStorage.ListUserFilesAsync(userPubKey);
                
                // Filter for posts only and exclude followers-only posts
                var posts = files.Where(f => 
                    f.Type == "post" && 
                    (f.ShareType != "followers" || string.IsNullOrEmpty(f.ShareType))
                ).ToList();
                
                return Ok(new
                {
                    Message = $"Found {posts.Count} non-follower posts out of {files.Count} total files",
                    Posts = posts,
                    TotalFiles = files.Count,
                    PostCount = posts.Count
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get timeline/feed of posts from multiple users
        /// </summary>
        /// <returns>Timeline of posts</returns>
        /// <response code="200">Returns the timeline</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("timeline")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetTimeline()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // For now, return user's own files
                // TODO: Implement following/friend system to aggregate posts from multiple users
                var files = await _fileStorage.ListUserFilesAsync(userPubKey);
                
                return Ok(new
                {
                    Message = "Timeline (currently user's own files only)",
                    Files = files,
                    Note = "Timeline aggregation from multiple users not yet implemented"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
} 