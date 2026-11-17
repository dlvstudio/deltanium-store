using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using NBitcoin;
using Microsoft.Extensions.Primitives;

namespace DeltaniumStore.Middleware
{
    public class SignatureVerificationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SignatureVerificationMiddleware> _logger;
        private static readonly TimeSpan _timestampValidityWindow = TimeSpan.FromMinutes(5);

        public SignatureVerificationMiddleware(RequestDelegate next, ILogger<SignatureVerificationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // CORS preflight has no signature headers; let UseCors answer it.
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    await _next(context);
                    return;
                }

                // Central-only proof/egress endpoints authenticate with the Central signature (ProofController
                // refuses them unless CentralRequestAuth marked the request). Reader receipts are public.
                if (context.Request.Path.StartsWithSegments("/api/proof"))
                {
                    if (!context.Request.Path.StartsWithSegments("/api/proof/egress/receipt"))
                    {
                        var http = context.RequestServices.GetService(typeof(System.Net.Http.IHttpClientFactory)) as System.Net.Http.IHttpClientFactory;
                        var config = (Microsoft.Extensions.Configuration.IConfiguration)context.RequestServices.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))!;
                        var (ok, err) = await DeltaniumStore.Security.CentralRequestAuth.AuthenticateAsync(context, config, http);
                        if (!ok) _logger.LogWarning("Internal call to {Path} refused: {Error}", context.Request.Path, err);
                    }
                    await _next(context);
                    return;
                }
                if (context.Request.Path.StartsWithSegments("/api/version"))
                {
                    await _next(context);
                    return;
                }

                if (!await VerifySignatureAsync(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid signature or missing required headers" });
                    return;
                }

                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying request signature");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Error verifying request signature" });
            }
        }

        private async Task<bool> VerifySignatureAsync(HttpContext context)
        {
            // Get and validate each header individually
            if (!context.Request.Headers.TryGetValue("X-User-PubKey", out var pubKeyHeader))
            {
                _logger.LogWarning("Missing X-User-PubKey header");
                return false;
            }

            if (!context.Request.Headers.TryGetValue("X-Timestamp", out var timestampHeader))
            {
                _logger.LogWarning("Missing X-Timestamp header");
                return false;
            }

            if (!context.Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
            {
                _logger.LogWarning("Missing X-Signature header");
                return false;
            }

            // Log all headers for debugging
            _logger.LogInformation("Signed request from {PubKey} at {Timestamp}", pubKeyHeader.ToString(), timestampHeader.ToString());

            var pubKeyBase64 = pubKeyHeader.ToString();
            var timestamp = timestampHeader.ToString();
            var signatureBase64 = signatureHeader.ToString();

            try
            {
                // Verify timestamp is within validity window
                if (!long.TryParse(timestamp, out var timestampValue))
                {
                    _logger.LogWarning("Invalid timestamp format: {0}", timestamp);
                    return false;
                }

                var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampValue);
                var now = DateTimeOffset.UtcNow;
                if (Math.Abs((now - requestTime).TotalMinutes) > _timestampValidityWindow.TotalMinutes)
                {
                    _logger.LogWarning("Request timestamp is outside validity window. Request time: {0}, Current time: {1}", requestTime, now);
                    return false;
                }

                // Get request body if present
                byte[] bodyBytes = null;
                if (context.Request.ContentLength > 0)
                {
                    // Enable request body buffering
                    context.Request.EnableBuffering();
                    
                    using (var memoryStream = new MemoryStream())
                    {
                        await context.Request.Body.CopyToAsync(memoryStream);
                        bodyBytes = memoryStream.ToArray();
                        
                        // Reset the request body stream position
                        context.Request.Body.Position = 0;
                    }

                    _logger.LogInformation("Request body length: {0} bytes", bodyBytes.Length);
                }

                // Recreate message string
                var messageBuilder = new StringBuilder();
                messageBuilder.Append(context.Request.Method.ToUpperInvariant());
                messageBuilder.Append(context.Request.Path.ToString());
                messageBuilder.Append(timestamp);

                if (bodyBytes != null && bodyBytes.Length > 0)
                {
                    using (var sha256 = SHA256.Create())
                    {
                        var bodyHash = sha256.ComputeHash(bodyBytes);
                        messageBuilder.Append(Convert.ToBase64String(bodyHash));
                    }
                }

                var message = messageBuilder.ToString();
                _logger.LogInformation("Message to verify: {0}", message);

                var messageBytes = Encoding.UTF8.GetBytes(NetworkScope.Scope(message));
                var messageHash = SHA256.HashData(messageBytes);
                _logger.LogInformation("Message hash: {0}", BitConverter.ToString(messageHash).Replace("-", ""));

                // Convert HEX public key to NBitcoin PubKey
                var pubKeyHex = pubKeyHeader.ToString();
                var pubKeyBytes = NBitcoin.DataEncoders.Encoders.Hex.DecodeData(pubKeyHex);
                var pubKey = new PubKey(pubKeyBytes);
                _logger.LogInformation("Public key: {0}", pubKey.ToHex());

                // Convert base64 signature to NBitcoin signature format
                var signatureBytes = Convert.FromBase64String(signatureBase64);
                _logger.LogInformation("Signature length: {0} bytes", signatureBytes.Length);
                
                // Verify signature
                var isValid = pubKey.Verify(new uint256(messageHash), signatureBytes);
                _logger.LogInformation("Signature verification result: {0}", isValid);
                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during signature verification");
                return false;
            }
        }
    }

    public static class SignatureVerificationMiddlewareExtensions
    {
        public static IApplicationBuilder UseSignatureVerification(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<SignatureVerificationMiddleware>();
        }
    }
} 