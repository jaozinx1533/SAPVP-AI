using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MinecraftProjectLauncher.Services;

public sealed class ModpackProfileState
{
    public string ActiveProfileId { get; set; } = "default";
    public List<ModpackProfile> Profiles { get; set; } = [new("default", "Padrão")];
}

public sealed record ModpackProfile(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Stores independent mod and client-configuration snapshots for a game version.</summary>
public static class ModpackProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ProfileDirectories = ["config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts"];

    private static string StatePath(string version) => Path.Combine(GameInstanceManager.GetInstancePath(version), "modpack-profiles.json");
    private static string SnapshotRoot(string version, string profileId) => Path.Combine(GameInstanceManager.GetInstancePath(version), "profiles", profileId);
    private static string SnapshotPath(string version, string profileId) => Path.Combine(SnapshotRoot(version, profileId), "mods");

    public static ModpackProfileState Load(string version)
    {
        if (!GameVersionCatalog.IsSupported(version)) throw new InvalidOperationException("A versão selecionada não é permitida neste launcher.");
        var path = StatePath(version);
        ModpackProfileState state;
        try
        {
            state = File.Exists(path)
                ? JsonSerializer.Deserialize<ModpackProfileState>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException) { state = new(); }
        state.Profiles ??= [];
        state.Profiles = state.Profiles
            .Where(profile => profile is not null && (profile.Id == "default" || Guid.TryParseExact(profile.Id, "N", out _)))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (state.Profiles.All(profile => profile.Id != "default")) state.Profiles.Insert(0, new("default", "Padrão"));
        if (state.Profiles.All(profile => profile.Id != state.ActiveProfileId)) state.ActiveProfileId = "default";
        return state;
    }

    public static IReadOnlyList<ModpackProfile> GetProfiles(string version) => Load(version).Profiles;

    public static ModpackProfile GetActiveProfile(string version)
    {
        var state = Load(version);
        return state.Profiles.First(profile => profile.Id == state.ActiveProfileId);
    }

    public static ModpackProfile CreateFromCurrent(string version, string name)
    {
        var cleanName = name.Trim();
        if (cleanName.Length is < 2 or > 40) throw new InvalidOperationException("Use um nome de perfil entre 2 e 40 caracteres.");
        if (cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("O nome do perfil contém caracteres inválidos.");
        var state = Load(version);
        if (state.Profiles.Any(profile => profile.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe um perfil com esse nome nesta versão.");

        var profile = new ModpackProfile(Guid.NewGuid().ToString("N"), cleanName);
        state.Profiles.Add(profile);
        CaptureCurrent(version, state.ActiveProfileId);
        CaptureCurrent(version, profile.Id);
        state.ActiveProfileId = profile.Id;
        Save(version, state);
        return profile;
    }

    public static void Activate(string version, string profileId)
    {
        var state = Load(version);
        var target = state.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new InvalidOperationException("O perfil selecionado não existe mais.");
        if (target.Id == state.ActiveProfileId) return;

        CaptureCurrent(version, state.ActiveProfileId);
        RestoreSnapshot(version, target.Id);
        state.ActiveProfileId = target.Id;
        Save(version, state);
    }

    public static int GetModCount(string version, string profileId)
    {
        var path = profileId == GetActiveProfile(version).Id
            ? Path.Combine(GameInstanceManager.GetInstancePath(version), "mods")
            : SnapshotPath(version, profileId);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.jar", SearchOption.TopDirectoryOnly).Length : 0;
    }

    public static void Export(string version, string profileId, string archivePath)
    {
        var profile = GetProfiles(version).FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("O perfil selecionado não existe mais.");
        if (profile.Id == GetActiveProfile(version).Id) CaptureCurrent(version, profileId);
        var snapshot = SnapshotRoot(version, profileId);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var manifest = JsonSerializer.Serialize(new { format = "sapvp-profile-v1", name = profile.Name, gameVersion = version });
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open())) writer.Write(manifest);
        if (!Directory.Exists(snapshot)) return;
        foreach (var file in Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(file, Path.GetRelativePath(snapshot, file).Replace('\\', '/'), CompressionLevel.Optimal);
    }

    public static ModpackProfile Import(string version, string archivePath)
    {
        if (!GameVersionCatalog.IsSupported(version)) throw new InvalidOperationException("A versão selecionada não é permitida.");
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("O arquivo não contém um perfil SAPVP-AI.");
        using var reader = new StreamReader(manifestEntry.Open());
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        var root = manifest.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != "sapvp-profile-v1") throw new InvalidDataException("Formato de perfil inválido.");
        if (!root.TryGetProperty("gameVersion", out var gameVersion) || gameVersion.GetString() != version) throw new InvalidDataException("Este perfil foi exportado para outra versão do Minecraft.");
        var name = root.TryGetProperty("name", out var profileName) ? profileName.GetString() ?? "Perfil importado" : "Perfil importado";
        var state = Load(version);
        if (state.Profiles.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name += " (importado)";
        var imported = new ModpackProfile(Guid.NewGuid().ToString("N"), name);
        var snapshot = SnapshotRoot(version, imported.Id);
        var allowedRoots = new HashSet<string>(ProfileDirectories.Append("mods").Append("options.txt"), StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == "manifest.json" || string.IsNullOrEmpty(entry.Name)) continue;
            if (++fileCount > 5000 || (totalBytes += entry.Length) > 1_500_000_000) throw new InvalidDataException("O arquivo de perfil excede os limites permitidos.");
            var relative = entry.FullName.Replace('\\', '/');
            var firstSegment = relative.Split('/')[0];
            if (!allowedRoots.Contains(firstSegment) || relative.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new InvalidDataException("O perfil contém um caminho de arquivo inválido.");
            var destination = Path.GetFullPath(Path.Combine(snapshot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(snapshot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O perfil contém um caminho inválido.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
        state.Profiles.Add(imported);
        Save(version, state);
        return imported;
    }

    public static ModpackProfile Rename(string version, string profileId, string name)
    {
        var cleanName = name.Trim();
        if (cleanName.Length is < 2 or > 40) throw new InvalidOperationException("Use um nome de perfil entre 2 e 40 caracteres.");
        if (cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("O nome do perfil contém caracteres inválidos.");
        var state = Load(version);
        var index = state.Profiles.FindIndex(profile => profile.Id == profileId);
        if (index < 0) throw new InvalidOperationException("O perfil selecionado não existe mais.");
        if (profileId == "default") throw new InvalidOperationException("O perfil Padrão não pode ser renomeado.");
        if (state.Profiles.Any(profile => profile.Id != profileId && profile.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe um perfil com esse nome nesta versão.");
        var renamed = state.Profiles[index] with { Name = cleanName };
        state.Profiles[index] = renamed;
        Save(version, state);
        return renamed;
    }

    public static void Delete(string version, string profileId)
    {
        var state = Load(version);
        var profile = state.Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("O perfil selecionado não existe mais.");
        if (profile.Id == "default") throw new InvalidOperationException("O perfil Padrão não pode ser excluído.");
        if (profile.Id == state.ActiveProfileId)
        {
            CaptureCurrent(version, state.ActiveProfileId);
            if (!Directory.Exists(SnapshotRoot(version, "default"))) CaptureCurrent(version, "default");
            RestoreSnapshot(version, "default");
            state.ActiveProfileId = "default";
        }
        state.Profiles.Remove(profile);
        var snapshotRoot = SnapshotRoot(version, profile.Id);
        if (Directory.Exists(snapshotRoot)) Directory.Delete(snapshotRoot, true);
        Save(version, state);
    }

    private static void CopyModJars(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var existing in Directory.GetFiles(destination, "*.jar", SearchOption.TopDirectoryOnly)) File.Delete(existing);
        if (Directory.Exists(source))
            foreach (var mod in Directory.GetFiles(source, "*.jar", SearchOption.TopDirectoryOnly))
                File.Copy(mod, Path.Combine(destination, Path.GetFileName(mod)), true);
        var sourceState = Path.Combine(source, "modrinth-installed.json");
        var destinationState = Path.Combine(destination, "modrinth-installed.json");
        if (File.Exists(sourceState)) File.Copy(sourceState, destinationState, true);
        else if (File.Exists(destinationState)) File.Delete(destinationState);
    }

    private static void CaptureCurrent(string version, string profileId)
    {
        var instancePath = GameInstanceManager.GetInstancePath(version);
        var snapshotRoot = SnapshotRoot(version, profileId);
        CopyModJars(GameInstanceManager.GetModsDirectory(version), Path.Combine(snapshotRoot, "mods"));
        foreach (var folder in ProfileDirectories)
            SyncDirectory(Path.Combine(instancePath, folder), Path.Combine(snapshotRoot, folder));
        SyncFile(Path.Combine(instancePath, "options.txt"), Path.Combine(snapshotRoot, "options.txt"));
        SyncFile(Path.Combine(instancePath, "modrinth-pack.json"), Path.Combine(snapshotRoot, "modrinth-pack.json"));
    }

    private static void RestoreSnapshot(string version, string profileId)
    {
        var instancePath = GameInstanceManager.GetInstancePath(version);
        var snapshotRoot = SnapshotRoot(version, profileId);
        CopyModJars(Path.Combine(snapshotRoot, "mods"), GameInstanceManager.GetModsDirectory(version));
        foreach (var folder in ProfileDirectories)
            SyncDirectory(Path.Combine(snapshotRoot, folder), Path.Combine(instancePath, folder));
        SyncFile(Path.Combine(snapshotRoot, "options.txt"), Path.Combine(instancePath, "options.txt"));
        SyncFile(Path.Combine(snapshotRoot, "modrinth-pack.json"), Path.Combine(instancePath, "modrinth-pack.json"));
    }

    private static void SyncDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void SyncFile(string source, string destination)
    {
        if (!File.Exists(source))
        {
            if (File.Exists(destination)) File.Delete(destination);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
    }

    private static void Save(string version, ModpackProfileState state)
    {
        var path = StatePath(version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, true);
    }
}
