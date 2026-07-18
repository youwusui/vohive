using System.Text.Json;

namespace VoHiveControl;

internal static class InstallLayout
{
    private const string StartScript = "Start VoHive WSL.ps1";
    private const string DefaultDistro = "VoHive";

    public static string ResolveToolScriptRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("VOHIVE_TOOL_ROOT");
        var installedTools = Path.Combine(AppContext.BaseDirectory, "Tools");
        var candidates = new[]
        {
            configuredRoot,
            Path.Combine(installedTools, "scripts"),
            installedTools,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "01_工具入口", "VoHive WSL 工具")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(fullPath, StartScript))) return fullPath;
        }

        return Path.Combine(installedTools, "scripts");
    }

    public static string ResolveDistroName()
    {
        var configuredDistro = Environment.GetEnvironmentVariable("VOHIVE_WSL_DISTRO");
        if (!string.IsNullOrWhiteSpace(configuredDistro)) return configuredDistro.Trim();

        foreach (var configPath in ResolveConfigCandidates())
        {
            if (!File.Exists(configPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (document.RootElement.TryGetProperty("distro", out var distro))
                {
                    var value = distro.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
            catch (JsonException)
            {
                // Startup scripts report malformed configuration with more context.
            }
        }

        return DefaultDistro;
    }

    private static IEnumerable<string> ResolveConfigCandidates()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("VOHIVE_TOOL_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var root = Path.GetFullPath(configuredRoot);
            yield return Path.Combine(root, "..", "config", "vohive-wsl.json");
            yield return Path.Combine(root, "config", "vohive-wsl.json");
        }

        var tools = Path.Combine(AppContext.BaseDirectory, "Tools");
        yield return Path.Combine(tools, "config", "vohive-wsl.json");
        yield return Path.Combine(tools, "config", "vohive-wsl.example.json");
    }
}
