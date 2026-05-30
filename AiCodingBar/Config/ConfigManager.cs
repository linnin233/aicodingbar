using System.IO;
using System.Text.Json;
using AiCodingBar.Models;

namespace AiCodingBar.Config;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aicoding-bar");
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
                else if (MigrateStateMapping(Current.StateMapping))
                    Save(); // 旧版 config 迁移成功，持久化到磁盘
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

    /// <summary>
    /// 旧版 config.json 兼容迁移：为 OneShot 事件补全 OneShotReturnState 字段。
    /// 旧版没有此字段，JSON 反序列化后为 null，导致回退逻辑走到"上一个 Persistent 状态"
    /// 而非预期的 idle，表现为完成/通知后残留 thinking/working 状态。
    /// 返回 true 表示有字段被补全，调用方应 Save() 持久化。
    /// </summary>
    private static bool MigrateStateMapping(Dictionary<string, StateMapping> mappings)
    {
        var defaults = ConfigModel.DefaultMappings();
        var updated = false;
        foreach (var (key, mapping) in mappings)
        {
            if (!string.IsNullOrEmpty(mapping.OneShotReturnState)) continue;
            if (defaults.TryGetValue(key, out var def) && !string.IsNullOrEmpty(def.OneShotReturnState))
            {
                mapping.OneShotReturnState = def.OneShotReturnState;
                updated = true;
            }
        }
        return updated;
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
