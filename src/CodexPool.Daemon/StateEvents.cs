using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexPool.Daemon;

public sealed class StateEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> clients = new();
    private string? last;
    private long revision;
    public async Task RunAsync(Func<object> status, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var snapshot = JsonSerializer.Serialize(status(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (snapshot == last) continue;
                last = snapshot; var message = "id: " + Interlocked.Increment(ref revision) + "\nevent: status\ndata: " + snapshot + "\n\n";
                foreach (var client in clients.Values) client.Writer.TryWrite(message);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { foreach (var client in clients.Values) client.Writer.TryComplete(); }
    }
    public async Task StreamAsync(HttpContext context, Func<object> status)
    {
        if (clients.Count >= 64) { context.Response.StatusCode = 503; return; }
        var id = Guid.NewGuid(); var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }); clients[id] = channel;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await context.Response.WriteAsync("event: status\ndata: " + JsonSerializer.Serialize(status(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted))
            { await context.Response.WriteAsync(message, context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); }
        }
        finally { clients.TryRemove(id, out _); channel.Writer.TryComplete(); }
    }
}
