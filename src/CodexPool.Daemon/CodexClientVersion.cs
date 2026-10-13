using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexPool.Daemon;

// Catalog client_version must match the Codex CLI that owns the picker.
// Prefer the query Codex already sends on GET /v1/models; otherwise resolve from env/CLI.
public static class CodexClientVersion
{
    public const string Fallback = "0.157.1"; // Last verified CLI protocol baseline.
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CliOutputPattern = new(@"\bcodex-cli\s+(\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? version) =>
        !string.IsNullOrWhiteSpace(version) && VersionPattern.IsMatch(version.Trim());

    public static string FromQueryOrFallback(string? queryVersion, string fallback)
    {
        if (IsValid(queryVersion)) return queryVersion!.Trim();
        return IsValid(fallback) ? fallback.Trim() : Fallback;
    }

    public static string ResolveInstalled(string? configuredEnv = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredEnv))
        {
            var version = configuredEnv.Trim();
            if (IsValid(version)) return version;
            return Fallback;
        }
        try
        {
            var startInfo = new ProcessStartInfo("codex")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                if (!process.WaitForExit(3000)) { process.Kill(); return Fallback; }
                var output = process.StandardOutput.ReadToEnd();
                var match = CliOutputPattern.Match(output);
                if (process.ExitCode == 0 && match.Success) return match.Groups[1].Value;
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        return Fallback;
    }
}
