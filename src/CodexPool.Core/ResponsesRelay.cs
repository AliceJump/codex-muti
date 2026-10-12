using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexPool.Core;

// A lease is fixed for one attempt. Control commands cannot mutate its target or token.
public sealed record ModelLease(string AccountId, Uri Endpoint, [property: JsonIgnore] string AccessToken, [property: JsonIgnore] string? BackendAccountId = null)
{
    public override string ToString() => $"ModelLease {{ AccountId = {AccountId}, AccessToken = [REDACTED] }}";
}
public sealed record RelayResult(string AccountId, int StatusCode, int Attempts, bool Committed, string Outcome, IReadOnlyList<string>? UpstreamHeaderNames = null, string? UpstreamContentType = null, string? PayloadKind = null, object? PrefixShape = null);

public interface IResponseSink
{
    void Start(int statusCode, string contentType);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

/// <summary>Model transport only. No tools, context reconstruction or implicit HTTP retries.</summary>
public sealed class ResponsesRelay(HttpClient http)
{
    private const int MaximumErrorBytes = 8192;
    private static readonly HashSet<string> QuotaCodes = new(StringComparer.Ordinal)
    { "usage_limit_reached", "quota_exhausted", "insufficient_quota", "subscription_sharing_usage_limit_exceeded" };
    private static readonly HashSet<string> AuthCodes = new(StringComparer.Ordinal)
    { "invalid_api_key", "invalid_token", "token_expired", "authentication_error", "unauthorized" };

    public async Task<RelayResult> ForwardAsync(
        ReadOnlyMemory<byte> body, string contentType, IReadOnlyList<ModelLease> candidates,
        IReadOnlyDictionary<string, string> headers, IResponseSink sink, CancellationToken cancellationToken,
        Action<ModelLease, string>? rejected = null, Action<ModelLease, bool>? attempt = null)
    {
        if (candidates.Count == 0) throw new ArgumentException("No candidate accounts.", nameof(candidates));
        if (candidates.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new ArgumentException("Each account may be attempted only once.", nameof(candidates));
        var committed = false;
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = candidates[index];
            using var observation = new AttemptObservation(lease, attempt);
            using var request = new HttpRequestMessage(HttpMethod.Post, lease.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.AccessToken);
            request.Headers.Accept.Add(new("text/event-stream"));
            // Account routing is constructed by the daemon, never trusted from downstream callers.
            if (lease.Endpoint.Host == "chatgpt.com" && lease.BackendAccountId is not null)
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", lease.BackendAccountId);
            request.Content = new ReadOnlyMemoryContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            foreach (var name in new[] { "session_id", "originator", "User-Agent", "OpenAI-Beta", "x-openai-internal-codex-responses-lite" })
                if (headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, value);
            // Turn state belongs to the original authenticated attempt; never reuse across failover.
            if (index == 0 && headers.TryGetValue("x-codex-turn-state", out var turnState))
                request.Headers.TryAddWithoutValidation("x-codex-turn-state", turnState);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { return new(lease.AccountId, 502, index + 1, committed, "upstream_unavailable"); }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadBoundedErrorAsync(response, cancellationToken);
                    var reason = ClassifyRejection(response.StatusCode, error);
                    if (reason is "quota_rejected" or "auth_rejected") rejected?.Invoke(lease, reason);
                    if (!committed && index + 1 < candidates.Count && reason is "quota_rejected" or "auth_rejected") continue;
                    // Never send upstream error bodies: they can echo prompts or credentials.
                    var safeError = JsonSerializer.SerializeToUtf8Bytes(new { error = new
                    { type = "proxy_upstream_error", code = reason, message = "The upstream model request was rejected." } });
                    sink.Start(status, "application/json");
                    committed = true;
                    await sink.WriteAsync(safeError, cancellationToken);
                    return new(lease.AccountId, status, index + 1, committed, reason);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[]? initialBytes = null;
                var upstreamType = response.Content.Headers.ContentType?.MediaType;
                if (upstreamType is null)
                {
                    // Observed on the authorized public route: valid SSE with no Content-Type header.
                    // Sniff typed SSE framing in a bounded prefix; event data can exceed this buffer.
                    // Codex's own SSE decoder validates the complete JSON event.
                    var prefix = new byte[8192]; var count = 0; var recognized = false;
                    using var prefixDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    prefixDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        while (count < prefix.Length)
                        {
                            var read = await stream.ReadAsync(prefix.AsMemory(count), prefixDeadline.Token);
                            if (read == 0) break; count += read;
                            if (RecognizesSse(prefix.AsSpan(0, count))) { recognized = true; break; }
                            var framed = System.Text.Encoding.UTF8.GetString(prefix, 0, count).Replace("\r\n", "\n", StringComparison.Ordinal);
                            var frameEnd = framed.LastIndexOf("\n\n", StringComparison.Ordinal);
                            if (frameEnd >= 0 && (framed[..frameEnd].StartsWith("data:", StringComparison.Ordinal) || framed[..frameEnd].Contains("\ndata:", StringComparison.Ordinal))) break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                    { return new(lease.AccountId, 502, index + 1, false, "stream_interrupted"); }
                    if (!recognized) return new(lease.AccountId, 502, index + 1, false, "unexpected_content_type",
                        HeaderNames(response), "missing", "unrecognized_prefix", DescribePrefix(prefix.AsSpan(0, count)));
                    initialBytes = prefix[..count];
                }
                else if (upstreamType != "text/event-stream")
                {
                    return new(lease.AccountId, 502, index + 1, false, "unexpected_content_type",
                        HeaderNames(response), upstreamType, "not_inspected");
                }
                var buffer = new byte[16 * 1024];
                var events = new RejectionEvents(lease, rejected);
                try
                {
                    if (initialBytes is not null)
                    {
                        sink.Start(status, "text/event-stream"); committed = true;
                        events.Feed(initialBytes);
                        await sink.WriteAsync(initialBytes, cancellationToken);
                    }
                    while (true)
                    {
                        var count = await stream.ReadAsync(buffer, cancellationToken);
                        if (count == 0) break;
                        if (!committed)
                        {
                            sink.Start(status, "text/event-stream");
                            // Latch BEFORE the write; a partial write can never reopen the gate.
                            committed = true;
                        }
                        events.Feed(buffer.AsSpan(0, count));
                        await sink.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    // EOF is never synthesized as response.completed. Codex owns recovery.
                    return new(lease.AccountId, status, index + 1, committed, committed ? "stream_forwarded" : "empty_stream",
                        HeaderNames(response), upstreamType ?? "missing", initialBytes is null ? null : "typed_sse");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                { return new(lease.AccountId, 502, index + 1, committed, "stream_interrupted"); }
            }
        }
        throw new InvalidOperationException("Candidate loop must return.");
    }

    private sealed class AttemptObservation : IDisposable
    {
        private readonly ModelLease lease;
        private readonly Action<ModelLease, bool>? observer;
        public AttemptObservation(ModelLease lease, Action<ModelLease, bool>? observer)
        { this.lease = lease; this.observer = observer; observer?.Invoke(lease, true); }
        public void Dispose() => observer?.Invoke(lease, false);
    }
    // Bounded metadata observer. Oversized frames are skipped, never retained or reconstructed.
    // A response.failed can exclude the account for future requests; this never opens replay.
    private sealed class RejectionEvents(ModelLease lease, Action<ModelLease, string>? observer)
    {
        private readonly MemoryStream line = new();
        private readonly MemoryStream data = new();
        private bool oversized;
        private bool reported;
        public void Feed(ReadOnlySpan<byte> bytes)
        {
            if (observer is null || reported) return;
            foreach (var b in bytes)
            {
                if (b == '\r') continue;
                if (b != '\n') { if (line.Length < 65536) line.WriteByte(b); else oversized = true; continue; }
                if (line.Length == 0)
                {
                    if (!oversized && data.Length > 0) Inspect();
                    data.SetLength(0); oversized = false;
                }
                else if (!oversized)
                {
                    var content = line.GetBuffer().AsSpan(0, (int)line.Length);
                    if (content.StartsWith("data:"u8))
                    {
                        if (data.Length + content.Length > 65536) oversized = true;
                        else { data.Write(content[5..]); data.WriteByte((byte)'\n'); }
                    }
                }
                line.SetLength(0);
            }
        }
        private void Inspect()
        {
            try
            {
                using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length)); var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() is not ("response.failed" or "error")) return;
                var container = root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object ? response : root;
                if (!container.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return;
                foreach (var field in new[] { "code", "type" })
                {
                    if (!error.TryGetProperty(field, out var code) || code.ValueKind != JsonValueKind.String) continue;
                    var reason = QuotaCodes.Contains(code.GetString()!) ? "quota_rejected" : AuthCodes.Contains(code.GetString()!) ? "auth_rejected" : null;
                    if (reason is not null) { reported = true; observer?.Invoke(lease, reason); return; }
                }
            }
            catch (JsonException) { }
        }
    }

    private static IReadOnlyList<string> HeaderNames(HttpResponseMessage response) =>
        response.Headers.Select(x => x.Key).Concat(response.Content.Headers.Select(x => x.Key)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool RecognizesSse(ReadOnlySpan<byte> prefix)
    {
        var text = System.Text.Encoding.UTF8.GetString(prefix).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        var typedHeader = lines.Any(x => x.StartsWith("event: response.", StringComparison.Ordinal) || x.TrimEnd() == "event: codex.rate_limits");
        if (typedHeader && lines.Any(x => x.StartsWith("data:", StringComparison.Ordinal) && x[5..].TrimStart().StartsWith('{'))) return true;
        var end = text.LastIndexOf("\n\n", StringComparison.Ordinal); if (end < 0) return false;
        foreach (var frame in text[..end].Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", frame.Split('\n').Where(x => x.StartsWith("data:", StringComparison.Ordinal)).Select(x => x[5..].TrimStart(' ')));
            if (data.Length == 0) continue;
            try
            {
                using var json = JsonDocument.Parse(data);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && (type.GetString()!.StartsWith("response.", StringComparison.Ordinal) || type.GetString() == "codex.rate_limits")) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }
    private static object DescribePrefix(ReadOnlySpan<byte> prefix)
    {
        var text = System.Text.Encoding.UTF8.GetString(prefix).Replace("\r\n", "\n", StringComparison.Ordinal);
        string? Safe(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-zA-Z0-9._-]{1,80}$") ? value : null;
        var eventNames = text.Split('\n').Where(x => x.StartsWith("event:", StringComparison.Ordinal)).Select(x => Safe(x[6..].Trim())).Where(x => x is not null).ToArray();
        var shapes = new List<object>();
        foreach (var line in text.Split('\n').Where(x => x.StartsWith("data:", StringComparison.Ordinal)))
        {
            try
            {
                using var json = JsonDocument.Parse(line[5..].Trim()); var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var fields = root.EnumerateObject().Select(x => Safe(x.Name)).Where(x => x is not null).ToArray();
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? Safe(t.GetString()) : null;
                string? errorType = null, errorCode = null;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("type", out var et) && et.ValueKind == JsonValueKind.String) errorType = Safe(et.GetString());
                    if (error.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String) errorCode = Safe(ec.GetString());
                }
                shapes.Add(new { fields, type, errorType, errorCode });
            }
            catch (JsonException) { }
        }
        return new { byteCount = prefix.Length, eventNames, shapes };
    }

    private static async Task<byte[]> ReadBoundedErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[MaximumErrorBytes + 1]; var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
        }
        catch (IOException) { return []; }
        catch (HttpRequestException) { return []; }
        return count > MaximumErrorBytes ? [] : bytes[..count];
    }
    private static string ClassifyRejection(HttpStatusCode status, byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return "unknown_rejection";
            foreach (var field in new[] { "code", "type" })
            {
                if (!error.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) continue;
                var code = value.GetString()!;
                if (status == HttpStatusCode.TooManyRequests && QuotaCodes.Contains(code)) return "quota_rejected";
                if (status == HttpStatusCode.Unauthorized && AuthCodes.Contains(code)) return "auth_rejected";
            }
        }
        catch (JsonException) { }
        return "unknown_rejection";
    }
}
