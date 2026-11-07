using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Configuration;

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
    }

    public class CentralApiService : ICentralApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _centralApiUrl;
        private readonly int _timeoutSeconds;
        private readonly string? _internalApiToken;

        public CentralApiService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _centralApiUrl = configuration["CentralApi:BaseUrl"] ?? "http://localhost:5002";
            _timeoutSeconds = configuration.GetValue<int>("CentralApi:Timeout", 30);
            _internalApiToken =
                Environment.GetEnvironmentVariable("INTERNAL_API_TOKEN")
                ?? configuration["InternalApi:Token"]
                ?? configuration["CentralApi:InternalToken"];
            
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
        /// Validate PRE token and return rk (base64) if authorized.
        /// </summary>
        public async Task<string?> ValidatePreTokenAsync(string token)
        {
            try
            {
                var url = $"{_centralApiUrl}/api/policy/validate-token";
                var payload = new Dictionary<string, string> { { "token", token } };
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(_internalApiToken))
                    request.Headers.TryAddWithoutValidation("X-Internal-Token", _internalApiToken);

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