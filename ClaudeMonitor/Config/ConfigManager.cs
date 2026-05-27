using System.IO;
using System.Text.Json;
using ClaudeMonitor.Models;

namespace ClaudeMonitor.Config;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".clawd-monitor");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string RuntimePath = Path.Combine(ConfigDir, "runtime.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ConfigModel Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Current = JsonSerializer.Deserialize<ConfigModel>(json, JsonOpts) ?? new ConfigModel();
                if (Current.StateMapping == null || Current.StateMapping.Count == 0)
                    Current.StateMapping = ConfigModel.DefaultMappings();
            }
            else
            {
                Current = new ConfigModel();
                Save();
            }
        }
        catch
        {
            Current = new ConfigModel();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    public void WriteRuntimePort(int port)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(new { port }, JsonOpts);
        File.WriteAllText(RuntimePath, json);
    }

    public static int ReadRuntimePort()
    {
        try
        {
            if (File.Exists(RuntimePath))
            {
                var json = File.ReadAllText(RuntimePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("port", out var port))
                    return port.GetInt32();
            }
        }
        catch { }
        return -1;
    }
}
