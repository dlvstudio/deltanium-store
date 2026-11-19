using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deltanium.DltpGateway;

/// <summary>DLTP v1 framing — keep identical to DeltaniumApi.Services.Dltp.DltpWire in the API repo.</summary>
public static class Wire
{
    public const string Preface = "DLTP/1\n";
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WritePrefaceAsync(Stream s, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(Preface), ct);
        await s.FlushAsync(ct);
    }

    public static async Task ExpectPrefaceAsync(Stream s, CancellationToken ct)
    {
        var expected = Encoding.ASCII.GetBytes(Preface);
        var got = new byte[expected.Length];
        await ReadExactlyAsync(s, got, ct);
        if (!got.AsSpan().SequenceEqual(expected)) throw new IOException("Not a DLTP/1 peer");
    }

    public static async Task WriteFrameAsync<T>(Stream s, T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaxFrameBytes) throw new IOException("DLTP frame too large");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await s.WriteAsync(header, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>Next frame, or null when the peer closed the connection cleanly between frames.</summary>
    public static async Task<T?> ReadFrameOrEndAsync<T>(Stream s, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        var first = await s.ReadAsync(header.AsMemory(0, 4), ct);
        if (first == 0) return null;
        if (first < 4) await ReadExactlyAsync(s, header.AsMemory(first), ct);
        var len = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len == 0 || len > MaxFrameBytes) throw new IOException("Bad DLTP frame length");
        var payload = new byte[len];
        await ReadExactlyAsync(s, payload, ct);
        return JsonSerializer.Deserialize<T>(payload, Json);
    }

    private static Task ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct) => ReadExactlyAsync(s, buffer.AsMemory(), ct);

    private static async Task ReadExactlyAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[read..], ct);
            if (n == 0) throw new EndOfStreamException("DLTP connection closed");
            read += n;
        }
    }
}

public class DltpRequest
{
    public long Id { get; set; }
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Body { get; set; }
}

public class DltpResponse
{
    public long Id { get; set; }
    public int Status { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Body { get; set; }
}
