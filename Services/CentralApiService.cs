using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Configuration;
using DeltaniumStore.Security;
using NBitcoin;

namespace DeltaniumStore.Services
{
    /// <summary>
    /// Service for communicating with the central Deltanium API
    /// Handles user management and following relationships
    /// </summary>
    public interface ICentralApiService
    {
        Task<List<string>> GetFollowingUsersAsync(string userPublicKey);
        Task<Dictionary<string, object>> GetUserInfoAsync(string userPublicKey);
        Task<string?> ValidatePreTokenAsync(string token);
        /// <summary>Tell Central the reactions and comments on an ended post, so it stops renewing them.</summary>
        Task<int> ReportChildrenEndedAsync(string parentFileId, IReadOnlyCollection<string> childFileIds);
    }


    public class CentralApiService : ICentralApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _centralApiUrl;
        private readonly int _timeoutSeconds;
        private readonly Func<Key?> _nodeKey;

        private static readonly Lazy<Key?> CachedNodeKey = new(() => NodeKey.Load());

        public CentralApiService(HttpClient httpClient, IConfiguration configuration, Func<Key?>? nodeKey = null)
            : this(httpClient, configuration)
        {
            if (nodeKey != null) _nodeKey = nodeKey;
        }

        [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
        public CentralApiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _centralApiUrl = configuration["CentralApi:BaseUrl"] ?? "http://localhost:5002";
            _timeoutSeconds = configuration.GetValue<int>("CentralApi:Timeout", 30);
            _nodeKey = () => CachedNodeKey.Value;
            
            // Configure HttpClient timeout
            _httpClient.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);
            
            Console.WriteLine($"[CentralApiService] Initialized with URL: {_centralApiUrl}, Timeout: {_timeoutSeconds}s");
        }

        /// <summary>
        /// Get list of public keys that the user is following
        /// </summary>
        public async Task<List<string>> GetFollowingUsersAsync(string userPublicKey)
        {
            try
            {
                Console.WriteLine($"[CentralApiService] Fetching following users for: {userPublicKey}");
                
                var response = await _httpClient.GetAsync($"{_centralApiUrl}/api/user/following?userPubKey={userPublicKey}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[CentralApiService] Response: {content}");
                    
                    var result = JsonSerializer.Deserialize<JsonElement>(content);
                    
                    // Parse the response to extract public keys
                    if (result.TryGetProperty("users", out var usersElement) && usersElement.ValueKind == JsonValueKind.Array)
                    {
                        var followingUsers = new List<string>();
                        
                        foreach (var userElement in usersElement.EnumerateArray())
                        {
                            if (userElement.TryGetProperty("publicKey", out var pubKeyElement))
                            {
                                var pubKey = pubKeyElement.GetString();
                                if (!string.IsNullOrEmpty(pubKey))
                                {
                                    followingUsers.Add(pubKey);
                                }
                            }
                        }
                        
                        Console.WriteLine($"[CentralApiService] Found {followingUsers.Count} following users");
                        return followingUsers;
                    }
                }
                else
                {
                    Console.WriteLine($"[CentralApiService] API request failed: {response.StatusCode} - {response.ReasonPhrase}");
                }
                
                return new List<string>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CentralApiService] Error fetching following users: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Get user information from central API
        /// </summary>
        public async Task<Dictionary<string, object>> GetUserInfoAsync(string userPublicKey)
        {
            try
            {
                Console.WriteLine($"[CentralApiService] Fetching user info for: {userPublicKey}");
                
                var response = await _httpClient.GetAsync($"{_centralApiUrl}/api/user/{userPublicKey}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                    return result ?? new Dictionary<string, object>();
                }
                else
                {
                    Console.WriteLine($"[CentralApiService] User info request failed: {response.StatusCode}");
                }
                
                return new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CentralApiService] Error fetching user info: {ex.Message}");
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// X-User-PubKey / X-Timestamp / X-Signature over METHOD + path+query + timestamp + sha256hex(body),
        /// network-scoped: the format Central's SignatureAuthHelper verifies.
        /// </summary>
        public static void SignAsNode(HttpRequestMessage request, string body, Key key, DateTimeOffset? now = null)
        {
            var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
            var bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var message = NetworkScope.Scope(request.Method.Method.ToUpperInvariant() + request.RequestUri!.PathAndQuery + ts + bodyHash);
            var sig = key.Sign(new uint256(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message))));
            request.Headers.TryAddWithoutValidation("X-User-PubKey", key.PubKey.ToHex());
            request.Headers.TryAddWithoutValidation("X-Timestamp", ts);
            request.Headers.TryAddWithoutValidation("X-Signature", Convert.ToBase64String(sig.ToDER()));
        }

        private async Task<JsonElement?> PostSignedAsync(string path, string body)
        {
            var key = _nodeKey();
            if (key == null) return null;
            using var request = new HttpRequestMessage(HttpMethod.Post, _centralApiUrl.TrimEnd('/') + path)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            SignAsNode(request, body, key);
            var resp = await _httpClient.SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CentralApiService] {path}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        }

        public async Task<int> ReportChildrenEndedAsync(string parentFileId, IReadOnlyCollection<string> childFileIds)
        {
            if (childFileIds.Count == 0) return 0;
            try
            {
                var body = JsonSerializer.Serialize(new { parentFileId, fileIds = childFileIds });
                var doc = await PostSignedAsync("/api/storedfile/children-ended", body);
                return doc is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("stopped", out var s) && s.TryGetInt32(out var n) ? n : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CentralApiService] children-ended failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Validate PRE token and return rk (base64) if authorized.
        /// </summary>
        public async Task<string?> ValidatePreTokenAsync(string token)
        {
            try
            {
                var url = $"{_centralApiUrl}/api/policy/validate-token";
                var body = JsonSerializer.Serialize(new Dictionary<string, string> { { "token", token } });
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                };
                // Central only lets an active registered Store redeem PRE tokens: sign with this node's key.
                var key = _nodeKey();
                if (key == null)
                {
                    Console.WriteLine("[CentralApiService] ValidatePreToken: no node key configured");
                    return null;
                }
                SignAsNode(request, body, key);

                var resp = await _httpClient.SendAsync(request);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync();
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("rk", out var rkEl))
                {
                    return rkEl.GetString();
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CentralApiService] ValidatePreToken failed: {ex.Message}");
                return null;
            }
        }
    }
} 