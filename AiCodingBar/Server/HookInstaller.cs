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

    private static async Task<bool> RunInstallScript(string args)
    {
        try
        {
            // Find the hooks directory relative to the executable
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var hooksDir = FindHooksDir(baseDir);
            if (hooksDir == null) return false;

            var installJs = Path.Combine(hooksDir, "install.js");
            if (!File.Exists(installJs)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{installJs}\" {args}",
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
        // Development: baseDir is like ClaudeMonitor/bin/Debug/net8.0-windows/
        // We need to go up to the repo root to find hooks/
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

    /// <summary>
    /// Ensure hooks are installed. Returns true if hooks are active after this call.
    /// </summary>
    public static async Task<bool> EnsureInstalledAsync()
    {
        if (IsInstalled()) return true;
        return await InstallAsync();
    }
}
