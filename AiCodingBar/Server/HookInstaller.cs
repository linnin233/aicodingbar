using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AiCodingBar.Server;

public class HookInstaller
{
    private static readonly string ClaudeSettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude");
    private static readonly string SettingsPath = Path.Combine(ClaudeSettingsDir, "settings.json");
    private const string MarkerKey = "__aicoding_bar__";

    // ── Claude Code Hook ──

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var json = File.ReadAllText(SettingsPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(MarkerKey, out _);
        }
        catch { return false; }
    }

    public static async Task<bool> InstallAsync()
    {
        return await RunInstallScript("install");
    }

    public static async Task<bool> UninstallAsync()
    {
        return await RunInstallScript("uninstall");
    }

    public static async Task<bool> EnsureInstalledAsync()
    {
        if (IsInstalled()) return true;
        return await InstallAsync();
    }

    // ── opencode Plugin ──

    private static readonly string OpencodeConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode");
    private static readonly string OpencodeConfigPath = Path.Combine(OpencodeConfigDir, "opencode.json");
    private const string OpencodePluginName = "aicoding-bar";

    public static bool IsOpencodePluginInstalled()
    {
        try
        {
            if (!File.Exists(OpencodeConfigPath)) return false;
            var json = File.ReadAllText(OpencodeConfigPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("plugin", out var plugins)) return false;
            foreach (var p in plugins.EnumerateArray())
            {
                var path = p.GetString();
                if (path != null && path.Contains(OpencodePluginName))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static async Task<bool> InstallOpencodePluginAsync()
    {
        return await RunNodeScript("opencode-install.js");
    }

    public static async Task<bool> EnsureOpencodePluginInstalledAsync()
    {
        if (!Directory.Exists(OpencodeConfigDir)) return false; // opencode not installed
        if (IsOpencodePluginInstalled()) return true;
        return await InstallOpencodePluginAsync();
    }

    // ── Shared helpers ──

    private static async Task<bool> RunInstallScript(string args)
    {
        return await RunNodeScript($"install.js {args}");
    }

    private static async Task<bool> RunNodeScript(string args)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var hooksDir = FindHooksDir(baseDir);
            if (hooksDir == null) return false;

            var script = Path.Combine(hooksDir, args.Split(' ')[0]);
            if (!File.Exists(script)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{script}\" {string.Join(" ", args.Split(' ').Skip(1))}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindHooksDir(string baseDir)
    {
        var dir = baseDir;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "hooks");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "install.js")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
        }
        return null;
    }
}
