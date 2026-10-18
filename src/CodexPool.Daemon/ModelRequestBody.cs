using ZstdSharp;
using ZstdSharp.Unsafe;

namespace CodexPool.Daemon;

public sealed class RequestBodyFailure(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class ModelRequestBody
{
    public const int MaximumBytes = 128 * 1024 * 1024;
    public static async Task<byte[]> ReadAsync(Stream source, string encoding, CancellationToken token, int maximumBytes = MaximumBytes)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (encoding.Length != 0 && !encoding.Equals("zstd", StringComparison.OrdinalIgnoreCase))
            throw new RequestBodyFailure("unsupported_content_encoding", 415);
        using var decoder = encoding.Length == 0 ? null : new DecompressionStream(source, leaveOpen: true);
        // Bound decoder history as well as output, including malicious frames with a huge window.
        decoder?.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 25);
        var input = (Stream?)decoder ?? source;
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                if (output.Length + read > maximumBytes) throw new RequestBodyFailure("request_body_too_large", 413);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ZstdException or EndOfStreamException)
        { throw new RequestBodyFailure("invalid_request_compression", 400); }
    }
}
