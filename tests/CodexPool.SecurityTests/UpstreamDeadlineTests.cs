using System.Net;
using CodexPool.Daemon;

internal static class UpstreamDeadlineTests
{
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException(); }
    private static Vault Store(string root, string name)
    {
        var vault = new Vault(Path.Combine(root, name));
        foreach (var id in new[] { "A", "B" }) vault.Add(new AccountCredential
        {
            Id = id, Name = id, Subject = "synthetic-" + id, ClientId = NativeLogin.NativeClientId, ProviderKind = "native",
            BackendAccountId = "backend-" + id, AccessToken = "synthetic-access-" + id, RefreshToken = "synthetic-refresh-" + id,
            IdToken = "synthetic-id-" + id, Scope = "native_codex", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), UpdatedAt = DateTimeOffset.UtcNow
        });
        return vault;
    }
    public static async Task QuotaBody(string root)
    {
        var vault = Store(root, "quota-body-deadline"); var blocked = new BlockingBody(); var requests = 0;
        using var http = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(++requests == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blocked) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true}}") })))
            { Timeout = TimeSpan.FromMilliseconds(50) };
        var quota = new Quota(http, new OAuth(http, vault), vault, TimeSpan.FromMilliseconds(250));
        await quota.RefreshAllAsync(new[] { "A", "B" }, default, force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Check(blocked.Disposed && requests == 2);
        Check(quota.RefreshErrors.Single().AccountId == "A" && quota.RefreshErrors.Single().Code == "quota_refresh_timeout");
        Check(quota.Snapshot("B")?.Fresh(DateTimeOffset.UtcNow) == true && vault.Find("A")!.AuthStatus == "ready");
        await quota.RefreshAsync("A", default, force: true); Check(quota.RefreshErrors.Length == 0);
    }
    public static async Task CatalogBody(string root)
    {
        var vault = Store(root, "catalog-body-deadline"); var blocked = new BlockingBody();
        using var http = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(blocked) }))) { Timeout = TimeSpan.FromMilliseconds(50) };
        var auth = new OAuth(http, vault); var pool = new PoolRuntime(vault, auth, new Quota(http, auth, vault), true);
        var catalog = new ModelCatalog(http, pool, TimeSpan.FromMilliseconds(250));
        try { await catalog.ReadAsync("0.162.1", default).WaitAsync(TimeSpan.FromSeconds(5)); Check(false); }
        catch (ModelCatalogFailure ex) { Check(ex.Error.Status == 504 && ex.Error.Code == "model_catalog_timeout"); }
        Check(blocked.Disposed && pool.Delete("B", out _));

        using var caller = new CancellationTokenSource(); caller.Cancel();
        try { await catalog.ReadAsync("0.162.1", caller.Token); Check(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
    }
    private sealed class BlockingBody : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
