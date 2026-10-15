using System.Text;

namespace CodexPool.Bridge;

internal sealed class BoundedJsonLines(TextReader reader, int limit)
{
    private readonly char[] buffer = new char[4096];
    private int position;
    private int length;
    public async Task<string?> ReadAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (position == length)
            {
                length = await reader.ReadAsync(buffer.AsMemory(), token); position = 0;
                if (length == 0) return line.Length == 0 ? null : throw new BridgeFailure("truncated_protocol_frame");
            }
            var newline = Array.IndexOf(buffer, '\n', position, length - position);
            var count = (newline < 0 ? length : newline) - position;
            if (line.Length > limit - count) throw new BridgeFailure("protocol_frame_limit");
            line.Append(buffer, position, count); position += count;
            if (newline < 0) continue;
            position++;
            if (line.Length > 0 && line[^1] == '\r') line.Length--;
            return line.ToString();
        }
    }
}
