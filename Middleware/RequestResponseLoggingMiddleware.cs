using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DeltaniumStore.Middleware
{
    public class RequestResponseLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestResponseLoggingMiddleware> _logger;

        public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            var correlationId = context.TraceIdentifier;
            var start = Stopwatch.GetTimestamp();
            var requestTimeUtc = DateTime.UtcNow;

            // Capture request
            context.Request.EnableBuffering();
            string requestBody = await ReadBodySafeAsync(context.Request.Body, leaveOpen: true);
            context.Request.Body.Position = 0;

            // Swap response body to capture
            var originalBody = context.Response.Body;
            await using var memStream = new MemoryStream();
            context.Response.Body = memStream;

            int statusCode = 0;
            string responseBody = string.Empty;
            try
            {
                await _next(context);
                statusCode = context.Response.StatusCode;
            }
            finally
            {
                memStream.Position = 0;
                responseBody = await ReadBodySafeAsync(memStream, leaveOpen: true);
                memStream.Position = 0;
                await memStream.CopyToAsync(originalBody);
                context.Response.Body = originalBody;
            }

            var durationMs = GetElapsedMilliseconds(start, Stopwatch.GetTimestamp());

            // Controller/Action if available
            var endpoint = context.GetEndpoint();
            var displayName = endpoint?.DisplayName ?? string.Empty;
            var clientIp = context.Connection.RemoteIpAddress?.ToString();

            var url = Redact(context.Request.Path + context.Request.QueryString.ToString());
            var userPk = context.Request.Headers["X-User-PubKey"].ToString();

            _logger.LogInformation(
                "URL={Url} METHOD={Method} RES({StatusCode})={ResBody} REQ={ReqBody} | CID={CorrelationId} Started={Started} DurMs={Duration} Controller={Controller} IP={ClientIp} UserPk={UserPk}",
                url,
                context.Request.Method,
                statusCode,
                Truncate(Redact(responseBody), 2000),
                Truncate(Redact(requestBody), 2000),
                correlationId,
                requestTimeUtc.ToString("HH:mm:ss.fff"),
                durationMs,
                displayName,
                clientIp,
                userPk
            );
        }

        private static async Task<string> ReadBodySafeAsync(Stream body, bool leaveOpen)
        {
            try
            {
                using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: leaveOpen);
                var text = await reader.ReadToEndAsync();
                if (LooksBinary(text)) return $"<binary {body.Length} bytes>";
                return text;
            }
            catch
            {
                return "<unavailable>";
            }
        }

        private static bool LooksBinary(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int control = text.Count(ch => ch < 0x09 || (ch > 0x0D && ch < 0x20));
            return control > 0;
        }

        // Never let keys or secrets reach the log files (mnemonics, private keys, tokens, passwords).
        private static readonly System.Text.RegularExpressions.Regex SecretJsonField = new(
            "(\"[^\"]*(?:mnemonic|private_?key|secret|password|token|api_?key)[^\"]*\"\\s*:\\s*)(\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\]\\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex SecretQueryParam = new(
            "([?&][^=&]*(?:mnemonic|private|secret|password|token|api_?key)[^=&]*=)[^&]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string Redact(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            s = SecretJsonField.Replace(s, "$1\"***\"");
            return SecretQueryParam.Replace(s, "$1***");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static double GetElapsedMilliseconds(long start, long stop)
        {
            return (stop - start) * 1000.0 / Stopwatch.Frequency;
        }
    }

    public static class RequestResponseLoggingMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequestResponseLogging(this IApplicationBuilder app)
        {
            return app.UseMiddleware<RequestResponseLoggingMiddleware>();
        }
    }
}





