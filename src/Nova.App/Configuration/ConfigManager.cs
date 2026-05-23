using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.App.Configuration;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NovaConfig Config { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            Config.WorkspacePath = ResolveDefaultWorkspacePath();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            Config = JsonSerializer.Deserialize<NovaConfig>(json, JsonOptions) ?? new();
            if (string.IsNullOrEmpty(Config.WorkspacePath))
                Config.WorkspacePath = ResolveDefaultWorkspacePath();
        }
        catch
        {
            Config = new() { WorkspacePath = ResolveDefaultWorkspacePath() };
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
    }

    private static string ResolveDefaultWorkspacePath()
    {
        var exeDir = AppContext.BaseDirectory;
        var repoWorkspace = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..", "nova-workspace"));
        if (Directory.Exists(repoWorkspace))
            return repoWorkspace;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nova", "workspace");
    }
}
