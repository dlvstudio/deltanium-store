using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;

public class NodeRegistrationService
{
    private readonly string _apiUrl;
    private readonly string _endpoint;
    private readonly string? _tcpEndpoint;
    private readonly string _voucher;
    private readonly string _nodeInfoPath = "node_info.json";

    public NodeRegistrationService(string apiUrl, string endpoint, string voucher, string? tcpEndpoint = null)
    {
        _apiUrl = apiUrl;
        _endpoint = endpoint;
        _tcpEndpoint = string.IsNullOrWhiteSpace(tcpEndpoint) ? null : tcpEndpoint.Trim();
        _voucher = voucher;
    }

    public async Task RegisterNodeIfNeededAsync()
    {
        if (File.Exists(_nodeInfoPath))
            return; // Already registered

        // Key: NODE_PRIVATE_KEY (DPAPI env, set up front by the operator) if present, so the key is never
        // written to disk; otherwise generate one (stored in node_info.json until moved to DPAPI).
        Key privateKey;
        Mnemonic? mnemonic = null;
        var envKey = Environment.GetEnvironmentVariable("NODE_PRIVATE_KEY")?.Trim();
        if (!string.IsNullOrEmpty(envKey))
        {
            Console.WriteLine("[REGISTRATION] Registering with the key from NODE_PRIVATE_KEY (DPAPI env).");
            privateKey = new Key(Convert.FromHexString(envKey));
        }
        else
        {
            mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            privateKey = mnemonic.DeriveExtKey().PrivateKey;
        }
        var publicKey = privateKey.PubKey;
        var publicKeyHex = publicKey.ToHex();

        var payload = new
        {
            endpoint = _endpoint,
            tcpEndpoint = _tcpEndpoint,
            voucher = _voucher,
            publicKey = publicKeyHex
        };

        using var client = new HttpClient();
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{_apiUrl}/api/storenode/register", content);

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            // Save mnemonic, privateKey, publicKey, and node info
            var apiResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
            object nodeInfo = mnemonic == null
                ? new { publicKey = publicKeyHex, apiResponse }
                : new { mnemonic = mnemonic.ToString(), privateKey = privateKey.ToHex(), publicKey = publicKeyHex, apiResponse };
            File.WriteAllText(_nodeInfoPath, JsonSerializer.Serialize(nodeInfo, new JsonSerializerOptions { WriteIndented = true }));
            if (mnemonic != null)
                Console.WriteLine("[REGISTRATION] node_info.json now holds the node key in plaintext. Move it to DPAPI with scripts/deploy/Move-NodeKeysToDpapi.ps1.");
            Console.WriteLine("Store node registered successfully.");
        }
        else
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Node registration failed: {error}");
            throw new Exception($"Node registration failed: {error}");
        }
    }
} 