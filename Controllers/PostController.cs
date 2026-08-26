using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using DeltaniumStore.Services;
using DeltaniumStore.Models;
using System.Text.Json;
using System.Linq;
using System.Net.Http;

namespace DeltaniumStore.Controllers
{
    /// <summary>
    /// Controller for managing post operations in Deltanium Store
    /// Posts are special files with type='post' in their metadata
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class PostController : ControllerBase
    {
        private readonly IFileStorageService _fileStorage;
        private readonly ICentralApiService _centralApiService;

        public PostController(IFileStorageService fileStorage, ICentralApiService centralApiService)
        {
            _fileStorage = fileStorage;
            _centralApiService = centralApiService;
        }

        // 🗑️ REMOVED: CreatePost() endpoint
        // Posts are created using regular file upload (FileController.upload-metadata-raw)
        // with type='post' in metadata. No need for separate endpoint.

        /// <summary>
        /// Get reactions summary for a post (counts per kind, total, my reaction)
        /// </summary>
        [HttpGet("reactions/{postFileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetReactions(string postFileId)
        {
            var userPubKey = Request.Headers["X-User-PubKey"].ToString();
            var normalizedUser = string.Empty;
            try { normalizedUser = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(userPubKey); } catch { normalizedUser = userPubKey; }

            var reactions = await _fileStorage.ListReactionsByParentAsync(postFileId);
            var byKind = reactions
                .GroupBy(r => (r.Kind ?? "unknown").ToLower())
                .ToDictionary(g => g.Key, g => g.Count());

            string? myReaction = null;
            if (!string.IsNullOrEmpty(normalizedUser))
            {
                myReaction = reactions
                    .FirstOrDefault(r => !string.IsNullOrEmpty(r.OwnerPubKey) &&
                                         DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(r.OwnerPubKey, normalizedUser))
                    ?.Kind;
            }

            return Ok(new
            {
                PostFileId = postFileId,
                Total = reactions.Count,
                ByKind = byKind,
                MyReaction = myReaction,
            });
        }

        /// <summary>
        /// Remove current user's reaction (optionally by kind) for a post
        /// </summary>
        [HttpDelete("reactions/{postFileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> RemoveReaction(string postFileId, [FromQuery] string? kind = null)
        {
            var userPubKey = Request.Headers["X-User-PubKey"].ToString();
            if (string.IsNullOrEmpty(userPubKey)) return BadRequest("User public key is required");
            var normalizedUser = string.Empty;
            try { normalizedUser = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(userPubKey); } catch { normalizedUser = userPubKey; }

            var reactions = await _fileStorage.ListReactionsByParentAsync(postFileId);
            var mine = reactions.Where(r => !string.IsNullOrEmpty(r.OwnerPubKey) &&
                                            DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(r.OwnerPubKey, normalizedUser));
            if (!string.IsNullOrEmpty(kind))
            {
                mine = mine.Where(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
            }

            int removed = 0;
            foreach (var r in mine.ToList())
            {
                var ok = await _fileStorage.DeleteFileByFileIdAsync(r.FileId);
                if (ok) removed++;
            }

            return Ok(new { Removed = removed });
        }

        /// <summary>
        /// Get comments for a post
        /// Returns comments that the current user can access (public or shared with them)
        /// </summary>
        [HttpGet("comments/{postFileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetComments(string postFileId)
        {
            var userPubKey = Request.Headers["X-User-PubKey"].ToString();
            var normalizedUser = string.Empty;
            try { normalizedUser = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(userPubKey); } catch { normalizedUser = userPubKey; }

            Console.WriteLine($"[GetComments] Request for postFileId: {postFileId}, user: {normalizedUser.Substring(0, Math.Min(10, normalizedUser.Length))}...");
            
            var comments = await _fileStorage.ListCommentsByParentAsync(postFileId);
            Console.WriteLine($"[GetComments] ListCommentsByParentAsync returned {comments.Count} comments");
            
            // Filter comments that user can access:
            // 1. Public comments (IsPublic = true)
            // 2. Encrypted comments where user is recipient (RecipientPubKey matches)
            // 3. Encrypted comments where user is owner (OwnerPubKey matches)
            var accessibleComments = comments
                .Where(c => 
                    c.IsPublic || // Public comment
                    (!string.IsNullOrEmpty(c.RecipientPubKey) && DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(c.RecipientPubKey, normalizedUser)) || // User is recipient
                    (!string.IsNullOrEmpty(c.OwnerPubKey) && DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(c.OwnerPubKey, normalizedUser)) // User is comment owner
                )
                .Select(c => new
                {
                    c.FileId,
                    c.Type,
                    c.ParentFileId,
                    c.OwnerPubKey,
                    c.CreationTime,
                    c.IsPublic,
                    c.EncryptedType,
                    c.ShareType,
                    c.RecipientPubKey,
                    c.FirstBlockId,
                    // Convert byte[] to base64 string for JSON serialization
                    EncryptedKey = c.EncryptedKey != null && c.EncryptedKey.Length > 0 
                        ? Convert.ToBase64String(c.EncryptedKey) 
                        : string.Empty,
                })
                .ToList();

            Console.WriteLine($"[GetComments] After filtering, {accessibleComments.Count} comments accessible to user");
            foreach (var c in accessibleComments)
            {
                Console.WriteLine($"[GetComments] Accessible comment: FileId={c.FileId}, IsPublic={c.IsPublic}, OwnerPubKey={c.OwnerPubKey?.Substring(0, Math.Min(10, c.OwnerPubKey?.Length ?? 0))}...");
            }

            return Ok(new
            {
                PostFileId = postFileId,
                Comments = accessibleComments,
                Count = accessibleComments.Count,
            });
        }

        /// <summary>
        /// Delete a comment (only by comment owner)
        /// </summary>
        [HttpDelete("comments/{commentFileId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteComment(string commentFileId)
        {
            var userPubKey = Request.Headers["X-User-PubKey"].ToString();
            if (string.IsNullOrEmpty(userPubKey)) return BadRequest("User public key is required");
            var normalizedUser = string.Empty;
            try { normalizedUser = DeltaniumStore.Services.PublicKeyUtils.NormalizePublicKey(userPubKey); } catch { normalizedUser = userPubKey; }

            // Get comment metadata to verify ownership
            var comment = await _fileStorage.GetFileMetadataAsync(commentFileId, normalizedUser);
            if (comment == null)
            {
                return NotFound("Comment not found");
            }

            // Verify user is the comment owner
            if (string.IsNullOrEmpty(comment.OwnerPubKey) || 
                !DeltaniumStore.Services.PublicKeyUtils.AreKeysEqual(comment.OwnerPubKey, normalizedUser))
            {
                return Forbid("Only comment owner can delete the comment");
            }

            var ok = await _fileStorage.DeleteFileByFileIdAsync(commentFileId);
            if (ok)
            {
                return Ok(new { Deleted = true, CommentFileId = commentFileId });
            }
            else
            {
                return StatusCode(500, new { Error = "Failed to delete comment" });
            }
        }

        /// <summary>
        /// Get user's posts (files with type='post')
        /// </summary>
        /// <param name="userPubKey">Public key of the user</param>
        /// <returns>List of user's posts</returns>
        /// <response code="200">Returns the list of posts</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("user/{userPubKey}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetUserPosts(string userPubKey)
        {
            try
            {
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                var files = await _fileStorage.ListUserFilesAsync(userPubKey);
                
                // 🆕 Now we can filter posts at storage level since type is in metadata
                var posts = files.Where(f => f.Type == "post").Select(post => new {
                    post.FileId,
                    post.Type,
                    post.FileName,
                    post.FileSize,
                    post.CreationTime,
                    post.IsPublic,
                    post.RecipientPubKey,
                    post.FirstBlockId,
                    post.EncryptedKey,
                    post.Version,
                    post.ShareType,
                    post.EncryptedType
                }).ToList();
                
                return Ok(new
                {
                    UserPubKey = userPubKey,
                    Posts = posts,
                    Message = $"Found {posts.Count} posts for user",
                    PostCount = posts.Count,
                    TotalFiles = files.Count
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get timeline/feed of posts (placeholder for future social features)
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

                // Get user's own posts for personal timeline
                var files = await _fileStorage.ListUserFilesAsync(userPubKey);
                var posts = files.Where(f => f.Type == "post").Select(post => new {
                    post.FileId,
                    post.Type,
                    post.FileName,
                    post.FileSize,
                    post.CreationTime,
                    post.IsPublic,
                    post.RecipientPubKey,
                    post.FirstBlockId,
                    post.EncryptedKey,
                    post.Version,
                    post.ShareType,
                    post.EncryptedType
                }).ToList();
                
                return Ok(new
                {
                    Message = "Personal timeline (own posts only)",
                    Posts = posts,
                    PostCount = posts.Count,
                    TotalFiles = files.Count,
                    Note = "Social timeline aggregation not yet implemented",
                    TODO = "Add following system and multi-user post aggregation"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get following feed - posts from users that the current user follows
        /// Returns both public posts and posts shared with the current user
        /// </summary>
        /// <param name="limit">Maximum number of posts to return</param>
        /// <returns>Following feed of posts</returns>
        /// <response code="200">Returns the following feed</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("following-feed")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetFollowingFeed([FromQuery] int limit = 20)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // 1. Get following users from central API using service
                var followingUsers = await _centralApiService.GetFollowingUsersAsync(userPubKey);
                if (!followingUsers.Any())
                {
                    return Ok(new
                    {
                        Message = "No following users found",
                        Posts = new List<object>(),
                        PostCount = 0,
                        UserPublicKey = userPubKey,
                        Limit = limit
                    });
                }

                // 2. Aggregate posts from followed users
                var allPosts = new List<dynamic>();
                var totalAccessiblePosts = 0;

                foreach (var followedUserPubKey in followingUsers)
                {
                    try
                    {
                        // Get posts from this followed user
                        var userFiles = await _fileStorage.ListUserFilesAsync(followedUserPubKey);
                        var userPosts = userFiles.Where(f => f.Type == "post").ToList();

                        foreach (var post in userPosts)
                        {
                            // Check if current user can access this post
                            var hasAccess = CanUserAccessPost(post, userPubKey);
                            if (hasAccess)
                            {
                                var postInfo = new
                                {
                                    FileId = post.FileId,
                                    AuthorPublicKey = followedUserPubKey,
                                    CreatedAt = post.CreationTime,
                                    IsPublic = post.IsPublic,
                                    Type = post.Type,
                                    FileName = post.FileName,
                                    Size = post.FileSize,
                                    HasAccess = true,
                                    EncryptedType = post.EncryptedType
                                };
                                allPosts.Add(postInfo);
                                totalAccessiblePosts++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue with other users
                        Console.WriteLine($"Error fetching posts from user {followedUserPubKey}: {ex.Message}");
                    }
                }

                // 3. Sort by creation time (newest first) and apply limit
                var sortedPosts = allPosts
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(limit)
                    .ToList();

                return Ok(new
                {
                    Message = "Following feed loaded successfully",
                    Posts = sortedPosts,
                    PostCount = sortedPosts.Count,
                    TotalAccessiblePosts = totalAccessiblePosts,
                    FollowingUsersCount = followingUsers.Count,
                    UserPublicKey = userPubKey,
                    Limit = limit,
                    Implementation = "Server-side aggregation with access control"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get detailed following feed with post content and metadata
        /// This endpoint fetches full post data including content for each accessible post
        /// </summary>
        /// <param name="limit">Maximum number of posts to return</param>
        /// <returns>Following feed with full post content</returns>
        /// <response code="200">Returns the detailed following feed</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("following-feed-detailed")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetDetailedFollowingFeed([FromQuery] int limit = 20)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // 1. Get following users from central API
                var followingUsers = await _centralApiService.GetFollowingUsersAsync(userPubKey);
                if (!followingUsers.Any())
                {
                    return Ok(new
                    {
                        Message = "No following users found",
                        Posts = new List<object>(),
                        PostCount = 0,
                        UserPublicKey = userPubKey,
                        Limit = limit
                    });
                }

                // 2. Aggregate posts with full content from followed users
                var allPosts = new List<dynamic>();
                var totalAccessiblePosts = 0;

                foreach (var followedUserPubKey in followingUsers)
                {
                    try
                    {
                        // Get posts from this followed user
                        var userFiles = await _fileStorage.ListUserFilesAsync(followedUserPubKey);
                        var userPosts = userFiles.Where(f => f.Type == "post").ToList();

                        foreach (var post in userPosts)
                        {
                            // Check if current user can access this post
                            var hasAccess = CanUserAccessPost(post, userPubKey);
                            if (hasAccess)
                            {
                                try
                                {
                                    // Get post metadata and content
                                    var postContent = await GetPostContentAsync(post.FileId, post.IsPublic);
                                    
                                    // Get author info from central API  
                                    var authorInfo = await _centralApiService.GetUserInfoAsync(followedUserPubKey);
                                    
                                    var detailedPost = new
                                    {
                                        FileId = post.FileId,
                                        AuthorPublicKey = followedUserPubKey,
                                        AuthorDisplayName = authorInfo.ContainsKey("fullName") ? authorInfo["fullName"]?.ToString() : null,
                                        AuthorAvatarUrl = authorInfo.ContainsKey("avatarUrl") ? authorInfo["avatarUrl"]?.ToString() : null,
                                        CreatedAt = post.CreationTime,
                                        IsPublic = post.IsPublic,
                                        Type = post.Type,
                                        FileName = post.FileName,
                                        Size = post.FileSize,
                                        EncryptedType = post.EncryptedType
                                    };
                                    
                                    allPosts.Add(detailedPost);
                                    totalAccessiblePosts++;
                                }
                                catch (Exception contentEx)
                                {
                                    Console.WriteLine($"Error loading content for post {post.FileId}: {contentEx.Message}");
                                    // Include post without content if content loading fails
                                    var basicPost = new
                                    {
                                        FileId = post.FileId,
                                        AuthorPublicKey = followedUserPubKey,
                                        CreatedAt = post.CreationTime,
                                        IsPublic = post.IsPublic,
                                        Type = post.Type,
                                        EncryptedType = post.EncryptedType
                                    };
                                    allPosts.Add(basicPost);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue with other users
                        Console.WriteLine($"Error fetching posts from user {followedUserPubKey}: {ex.Message}");
                    }
                }

                // 3. Sort by creation time (newest first) and apply limit
                var sortedPosts = allPosts
                    .OrderByDescending(p => p.CreatedAt)
                    .Take(limit)
                    .ToList();

                return Ok(new
                {
                    Message = "Detailed following feed loaded successfully",
                    Posts = sortedPosts,
                    PostCount = sortedPosts.Count,
                    TotalAccessiblePosts = totalAccessiblePosts,
                    FollowingUsersCount = followingUsers.Count,
                    UserPublicKey = userPubKey,
                    Limit = limit,
                    Implementation = "Server-side aggregation with full content and metadata"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a user can access a specific post
        /// </summary>
        private bool CanUserAccessPost(dynamic post, string userPubKey)
        {
            // Public posts are always accessible
            if (post.IsPublic)
            {
                return true;
            }

            // For private posts, check if user has encrypted key (meaning it was shared with them)
            // This is a simplified check - in reality, we'd need to verify the encrypted key
            try
            {
                // Check if post has shared access for this user
                // This would involve checking the file metadata for encrypted keys
                var hasSharedAccess = post.EncryptedKeys?.ContainsKey(userPubKey) ?? false;
                return hasSharedAccess;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Search posts by tags or content (placeholder - requires indexing)
        /// </summary>
        /// <param name="query">Search query</param>
        /// <returns>Search results</returns>
        /// <response code="200">Returns search results</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("search")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SearchPosts([FromQuery] string query)
        {
            try
            {
                if (string.IsNullOrEmpty(query))
                {
                    return BadRequest("Search query is required");
                }

                // TODO: Implement post search functionality
                // This would require:
                // 1. Indexing post content and tags
                // 2. Search engine integration
                // 3. Privacy-preserving search mechanisms
                
                return Ok(new
                {
                    Query = query,
                    Results = new List<object>(),
                    Message = "Post search not yet implemented",
                    Note = "Search requires indexing and privacy-preserving mechanisms"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper method to get post content (metadata + main content)
        /// </summary>
        private async Task<object> GetPostContentAsync(string fileId, bool isPublic)
        {
            try
            {
                // For now, return basic structure
                // In full implementation, this would:
                // 1. Load metadata block (index 0)
                // 2. Load content block (index 1) 
                // 3. Decrypt if needed
                // 4. Parse and return structured content
                
                return new
                {
                    TextContent = "Post content would be loaded here",
                    Tags = new List<string>(),
                    AttachedMedia = new List<object>(),
                    Note = "Content loading not fully implemented yet"
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading post content for {fileId}: {ex.Message}");
                return new
                {
                    Error = "Failed to load post content",
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Test endpoint to verify central API integration
        /// </summary>
        /// <returns>Test results</returns>
        [HttpGet("test-central-api")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> TestCentralAPI()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required for testing");
                }

                // Test following users fetch
                var followingUsers = await _centralApiService.GetFollowingUsersAsync(userPubKey);
                
                // Test user info fetch
                var userInfo = await _centralApiService.GetUserInfoAsync(userPubKey);

                return Ok(new
                {
                    Message = "Central API integration test",
                    UserPublicKey = userPubKey,
                    FollowingUsersCount = followingUsers.Count,
                    FollowingUsers = followingUsers,
                    UserInfo = userInfo,
                    CentralApiUrl = Environment.GetEnvironmentVariable("CENTRAL_API_URL") ?? "http://localhost:5002",
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Error = "Central API test failed",
                    Message = ex.Message,
                    StackTrace = ex.StackTrace
                });
            }
        }

        /// <summary>
        /// Get list of stores for following feed (for distributed store architecture)
        /// Returns all stores (hardcoded for now) and following users
        /// </summary>
        /// <returns>List of stores and following users</returns>
        /// <response code="200">Returns the list of stores and following users</response>
        /// <response code="400">If the request is invalid</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("following-stores")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetFollowingStores()
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }

                // TODO: Replace with dynamic store discovery or config
                var stores = new List<object>
                {
                    new { StoreUrl = "https://store1.example.com", StoreName = "Store 1" },
                    new { StoreUrl = "https://store2.example.com", StoreName = "Store 2" }
                };

                // Get following users from central API
                var followingUsers = await _centralApiService.GetFollowingUsersAsync(userPubKey);

                return Ok(new
                {
                    Message = "List of stores for following feed",
                    Stores = stores,
                    FollowingUsers = followingUsers,
                    UserPublicKey = userPubKey,
                    StoreCount = stores.Count
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get posts shared with followers (union Option 1 & Option 2)
        /// </summary>
        [HttpGet("followers-feed")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetFollowersFeed([FromQuery] int limit = 20)
        {
            try
            {
                var userPubKey = Request.Headers["X-User-PubKey"].ToString();
                if (string.IsNullOrEmpty(userPubKey))
                {
                    return BadRequest("User public key is required");
                }
                // Lấy tất cả post có ShareType = "followers" từ storage (union Option 1 & 2)
                var allFollowerPosts = await _fileStorage.ListAllPostsByShareTypeAsync("followers");

                // Option 1: các post per-recipient (RecipientPubKey == userPubKey)
                var opt1 = allFollowerPosts
                    .Where(post => !string.IsNullOrEmpty(post.RecipientPubKey) && post.RecipientPubKey == userPubKey)
                    .Select(post => new {
                        post.FileId,
                        post.Type,
                        post.FileName,
                        post.FileSize,
                        post.CreationTime,
                        post.IsPublic,
                        post.RecipientPubKey,
                        post.FirstBlockId,
                        post.EncryptedKey,
                        post.Version,
                        post.ShareType,
                        post.EncryptedType,
                        PolicyTag = (string?)post.PolicyTag,
                        CapsuleFor = (string?)post.CapsuleFor,
                        PolicyScheme = (string?)post.PolicyScheme,
                        OwnerPubKey = (string?)post.OwnerPubKey,
                        EncapsulatedForRecipient = post.EncapsulatedForRecipient
                    });

                // Option 2: các post theo tag (capsuleFor == 'tag'), không có RecipientPubKey
                var opt2 = allFollowerPosts
                    .Where(post => string.IsNullOrEmpty(post.RecipientPubKey) && string.Equals(post.CapsuleFor, "tag", StringComparison.OrdinalIgnoreCase))
                    .Select(post => new {
                        post.FileId,
                        post.Type,
                        post.FileName,
                        post.FileSize,
                        post.CreationTime,
                        post.IsPublic,
                        post.RecipientPubKey,
                        post.FirstBlockId,
                        post.EncryptedKey,
                        post.Version,
                        post.ShareType,
                        post.EncryptedType,
                        PolicyTag = (string?)post.PolicyTag,
                        CapsuleFor = (string?)post.CapsuleFor,
                        PolicyScheme = (string?)post.PolicyScheme,
                        OwnerPubKey = (string?)post.OwnerPubKey,
                        EncapsulatedForRecipient = post.EncapsulatedForRecipient
                    });

                var union = opt1.Concat(opt2)
                    .OrderByDescending(p => p.CreationTime)
                    .Take(limit)
                    .ToList();

                return Ok(new
                {
                    Message = "Followers feed (union Option1 & Option2)",
                    Posts = union,
                    PostCount = union.Count,
                    UserPublicKey = userPubKey,
                    Limit = limit
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get public feed - all posts marked as ShareType = "public"
        /// Public posts are accessible to everyone
        /// </summary>
        /// <param name="limit">Maximum number of posts to return</param>
        /// <returns>Public feed of posts</returns>
        /// <response code="200">Returns the public feed</response>
        /// <response code="500">If there was an internal server error</response>
        [HttpGet("public-feed")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPublicFeed([FromQuery] int limit = 20)
        {
            try
            {
                // Fetch all public posts (IsPublic or EncryptedType == 'public')
                var allPublicPosts = await _fileStorage.ListAllPublicPostsAsync();

                var posts = allPublicPosts
                    .OrderByDescending(p => p.CreationTime)
                    .Take(limit)
                    .Select(post => new {
                        post.FileId,
                        post.Type,
                        post.FileName,
                        post.FileSize,
                        post.CreationTime,
                        post.IsPublic,
                        post.RecipientPubKey,
                        post.FirstBlockId,
                        post.EncryptedKey,
                        post.Version,
                        post.ShareType,
                        post.EncryptedType
                    })
                    .ToList();

                return Ok(new
                {
                    Message = "Public feed loaded successfully",
                    Posts = posts,
                    PostCount = posts.Count,
                    Limit = limit
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
} 