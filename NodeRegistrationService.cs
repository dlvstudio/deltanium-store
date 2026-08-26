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
    private readonly string _voucher;
    private readonly string _nodeInfoPath = "node_info.json";

    public NodeRegistrationService(string apiUrl, string endpoint, string voucher)
    {
        _apiUrl = apiUrl;
        _endpoint = endpoint;
        _voucher = voucher;
    }

    public async Task RegisterNodeIfNeededAsync()
    {
        if (File.Exists(_nodeInfoPath))
            return; // Already registered

        // Generate mnemonic and key pair
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var hdRoot = mnemonic.DeriveExtKey();
        var privateKey = hdRoot.PrivateKey;
        var publicKey = privateKey.PubKey;
        var publicKeyHex = publicKey.ToHex();

        var payload = new
        {
            endpoint = _endpoint,
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
            var nodeInfo = new
            {
                mnemonic = mnemonic.ToString(),
                privateKey = privateKey.ToHex(),
                publicKey = publicKeyHex,
                apiResponse = JsonSerializer.Deserialize<JsonElement>(responseBody)
            };
            File.WriteAllText(_nodeInfoPath, JsonSerializer.Serialize(nodeInfo, new JsonSerializerOptions { WriteIndented = true }));
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