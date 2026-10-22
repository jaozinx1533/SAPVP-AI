using System.IO;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace MinecraftProjectLauncher;

public sealed record GameInstanceInfo(
    string Version,
    string Directory,
    bool IsInstalled,
    int ModCount,
    string Name = "");

public static class GameInstanceManager
{
    public static string InstancesRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftProjectLauncher",
        "instances");

    public static string EnsureInstance(string version)
    {
        if (!GameVersionCatalog.IsSupported(version))
        {
            throw new InvalidOperationException("Esta versão não está autorizada para este launcher.");
        }

        var instancePath = GetInstancePath(version);
        Directory.CreateDirectory(instancePath);
        Directory.CreateDirectory(Path.Combine(instancePath, "mods"));
        Directory.CreateDirectory(Path.Combine(instancePath, "saves"));
        return instancePath;
    }

    public static string GetInstancePath(string version)
    {
        if (!GameVersionCatalog.IsSupported(version))
            throw new InvalidOperationException("Esta versão não está autorizada para este launcher.");
        return Path.Combine(InstancesRoot, version);
    }

    public static string GetModsDirectory(string version)
    {
        var root = EnsureInstance(version);
        return Path.Combine(root, "mods");
    }

    public static GameInstanceInfo CreateNamedInstance(string version, string name, string? loader = null)
    {
        if (!GameVersionCatalog.IsSupported(version)) throw new InvalidOperationException("Versão não permitida.");
        var cleanName = string.Join(" ", (name ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(cleanName)) throw new InvalidOperationException("Digite um nome válido para a instância.");
        var customRoot = Path.Combine(InstancesRoot, "custom");
        Directory.CreateDirectory(customRoot);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(customRoot, id);
        Directory.CreateDirectory(Path.Combine(path, "mods"));
        Directory.CreateDirectory(Path.Combine(path, "saves"));
        File.WriteAllText(Path.Combine(path, "sapvp-instance.json"), System.Text.Json.JsonSerializer.Serialize(new { id, name = cleanName, version, loader }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return new GameInstanceInfo(version, path, false, 0, cleanName);
    }

    public static GameInstanceInfo RenameInstance(GameInstanceInfo instance, string name)
    {
        if (!GameVersionCatalog.IsSupported(instance.Version)) throw new InvalidOperationException("Versão não permitida.");
        var fullPath = Path.GetFullPath(instance.Directory);
        var allowedRoot = Path.GetFullPath(InstancesRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullPath))
            throw new InvalidOperationException("A instância não pertence ao launcher ou não existe mais.");
        var cleanName = string.Join(" ", (name ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(cleanName)) throw new InvalidOperationException("Digite um nome válido para a instância.");

        var metadataPath = Path.Combine(fullPath, "sapvp-instance.json");
        var id = Guid.NewGuid().ToString("N");
        string? loader = null;
        if (File.Exists(metadataPath))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));
                if (document.RootElement.TryGetProperty("id", out var idElement)) id = idElement.GetString() ?? id;
                if (document.RootElement.TryGetProperty("loader", out var loaderElement)) loader = loaderElement.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }
        var temporaryPath = metadataPath + ".tmp";
        File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(new { id, name = cleanName, version = instance.Version, loader }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, metadataPath, true);
        return GetInstances().First(item => string.Equals(item.Directory, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<GameInstanceInfo> GetInstances()
    {
        var instances = new List<GameInstanceInfo>();
        foreach (var version in GameVersionCatalog.SupportedVersions)
        {
            var instancePath = GetInstancePath(version);
            if (!Directory.Exists(instancePath))
            {
                continue;
            }

            var versionDirectory = Path.Combine(instancePath, "versions", version);
            var installed = File.Exists(Path.Combine(versionDirectory, $"{version}.json"));
            var modsPath = Path.Combine(instancePath, "mods");
            var modCount = Directory.Exists(modsPath)
                ? Directory.GetFiles(modsPath, "*.jar", SearchOption.TopDirectoryOnly).Length
                : 0;

            // Empty folders left by older launcher builds are only scaffolding, not user instances.
            // Keep them on disk, but do not present them as created instances until they contain data.
            var hasUserData = modCount > 0
                || File.Exists(Path.Combine(instancePath, "modpack-profiles.json"))
                || File.Exists(Path.Combine(instancePath, "modrinth-pack.json"))
                || new[] { "saves", "mods", "config", "resourcepacks", "shaderpacks", "profiles" }
                    .Any(name => DirectoryHasFiles(Path.Combine(instancePath, name)));
            if (!installed && !hasUserData)
            {
                continue;
            }

            var displayName = $"Minecraft {version}";
            var metadataPath = Path.Combine(instancePath, "sapvp-instance.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));
                    if (metadata.RootElement.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString()))
                        displayName = nameElement.GetString()!;
                }
                catch (System.Text.Json.JsonException) { }
            }
            instances.Add(new GameInstanceInfo(version, instancePath, installed, modCount, displayName));
        }

        var customRoot = Path.Combine(InstancesRoot, "custom");
        if (Directory.Exists(customRoot))
        {
            foreach (var path in Directory.EnumerateDirectories(customRoot))
            {
                try
                {
                    var metadataPath = Path.Combine(path, "sapvp-instance.json");
                    if (!File.Exists(metadataPath)) continue;
                    using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = metadata.RootElement;
                    var name = root.GetProperty("name").GetString() ?? "Instância";
                    var version = root.GetProperty("version").GetString() ?? "";
                    if (!GameVersionCatalog.IsSupported(version)) continue;
                    var gamePath = Path.Combine(path, "versions", version, $"{version}.json");
                    var modsPath = Path.Combine(path, "mods");
                    var modCount = Directory.Exists(modsPath) ? Directory.GetFiles(modsPath, "*.jar", SearchOption.TopDirectoryOnly).Length : 0;
                    instances.Add(new GameInstanceInfo(version, path, File.Exists(gamePath), modCount, name));
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException) { }
            }
        }

        return instances.OrderBy(instance => instance.Version, StringComparer.OrdinalIgnoreCase).ThenBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DirectoryHasFiles(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static IReadOnlyList<string> GetMods(string version)
    {
        var folder = Path.Combine(GetInstancePath(version), "mods");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.jar", SearchOption.TopDirectoryOnly).Select(path => Path.GetFileName(path)!).ToArray()
            : Array.Empty<string>();
    }

    public static void MoveInstanceToRecycleBin(string version)
    {
        if (!GameVersionCatalog.IsSupported(version))
        {
            throw new InvalidOperationException("Esta versão não pertence às instâncias gerenciadas por este launcher.");
        }

        var instancePath = GetInstancePath(version);
        if (Directory.Exists(instancePath))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(instancePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    public static void MoveInstanceToRecycleBin(GameInstanceInfo instance)
    {
        if (!Directory.Exists(instance.Directory)) return;
        var fullPath = Path.GetFullPath(instance.Directory);
        var allowedRoot = Path.GetFullPath(InstancesRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A pasta não pertence às instâncias do launcher.");
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

}



