using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json.Nodes;

namespace CodexPool.Daemon;

// Discovery chooses an address; the pipe server's kernel identity remains the
// authority. No Windows build number or private Shell ABI belongs in a Provider.
internal sealed record QuickSettingsEndpoint(string PipeName, uint ProcessId, string ProcessCreation)
{
    internal static string LogonSid(WindowsIdentity identity)
    {
        // WindowsIdentity.Groups omits the SE_GROUP_LOGON_ID entries.
        GetTokenInformation(identity.AccessToken, 2, IntPtr.Zero, 0, out var size);
        if (size is < 8 or > 1048576) throw new IOException("Logon identity unavailable.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 2, buffer, size, out _))
                throw new IOException("Logon identity unavailable.");
            var count = (uint)Marshal.ReadInt32(buffer);
            var offset = Marshal.OffsetOf<TokenGroups>(nameof(TokenGroups.First)).ToInt32();
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count > 4096 || offset + (long)count * stride > size) throw new IOException("Invalid token groups.");
            string? logon = null;
            for (var i = 0u; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(buffer + offset + checked((int)i * stride));
                if ((group.Attributes & 0xC0000000) != 0xC0000000) continue;
                if (logon is not null || group.Sid == IntPtr.Zero) throw new IOException("Invalid logon identity.");
                logon = new SecurityIdentifier(group.Sid).Value;
            }
            return logon ?? throw new IOException("Logon identity unavailable.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups { public uint Count; public SidAndAttributes First; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int returnedLength);

    internal static string FileName(string broker, string user, int session) => Path.Combine(
        Path.GetDirectoryName(broker) ?? throw new IOException("Broker directory unavailable."),
        Path.GetFileName(broker) + ".endpoint-v1." + user + "." + session.ToString(CultureInfo.InvariantCulture) + ".json");

    internal static QuickSettingsEndpoint? Read(string broker, string user, string logon, int session, string legacyName)
    {
        var path = FileName(broker, user, session);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Unsafe discovery record.");
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (source.Length is <= 0 or > 16384) throw new IOException("Invalid discovery size.");
            // Bounded reads also reject a concurrently expanded record.
            var bytes = new byte[16385];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = source.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count is 0 or > 16384) throw new IOException("Invalid discovery size.");
            return Parse(JsonNode.Parse(bytes.AsSpan(0, count))?.AsObject()
                ?? throw new IOException("Invalid discovery record."), broker, user, logon, session, legacyName);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException
            or FormatException or OverflowException or ArgumentException)
        { throw new IOException("Invalid discovery record."); }
        // A present invalid/stale record must not silently fall back elsewhere.
    }

    internal static QuickSettingsEndpoint Parse(JsonObject value, string broker, string user,
        string logon, int session, string legacyName)
    {
        if (value.Count != 8 || value["discoveryVersion"]?.GetValue<int>() != 1
            || !string.Equals(value["brokerPath"]?.GetValue<string>(), broker, StringComparison.OrdinalIgnoreCase)
            || value["userSid"]?.GetValue<string>() != user || value["logonSid"]?.GetValue<string>() != logon
            || value["session"]?.GetValue<int>() != session
            || value["processId"] is not JsonValue pidValue || !pidValue.TryGetValue<uint>(out var pid) || pid == 0
            || value["processCreation"]?.GetValue<string>() is not { Length: > 0 and <= 20 } created
            || created.Any(x => x is < '0' or > '9')
            || value["pipeName"]?.GetValue<string>() is not { Length: > 0 and <= 1024 } pipe || pipe.Contains('\0'))
            throw new IOException("Invalid discovery identity.");
        var prefix = @"\\.\pipe\";
        var expected = "QuickSettingsExtensions.v1." + user + "." + session.ToString(CultureInfo.InvariantCulture) + ".";
        if (!pipe.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Remote discovery rejected.");
        var name = pipe[prefix.Length..];
        var desktop = name.StartsWith(expected, StringComparison.Ordinal) && name.EndsWith("." + logon, StringComparison.Ordinal)
            && name.AsSpan(expected.Length).IndexOfAny('/', '\\') < 0;
        if (!desktop && name != legacyName) throw new IOException("Unexpected pipe namespace.");
        return new(name, pid, created);
    }

    internal void Check(Process server)
    {
        if (server.Id != ProcessId || server.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) != ProcessCreation)
            throw new IOException("Stale discovery record.");
    }
}
