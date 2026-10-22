using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace MinecraftProjectLauncher.Services;

public static class GameInstanceArchiveService
{
    private static readonly string[] IncludedDirectories = ["mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts", "saves", "screenshots", "profiles"];
    private static readonly HashSet<string> IncludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "servers.dat", "modrinth-pack.json", "modrinth-installed.json", "modpack-profiles.json"
    };
    private const long MaximumUncompressedBytes = 20L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static (string Name, string Version) ReadPackageInfo(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("sapvp-instance.json") ?? throw new InvalidDataException("O pacote não contém o manifesto SAPVP-AI.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<InstanceArchiveManifest>(stream, Json)
            ?? throw new InvalidDataException("O manifesto da instância está vazio ou inválido.");
        if (manifest.Format != 1 || !GameVersionCatalog.IsSupported(manifest.Version)) throw new InvalidDataException("O pacote usa um formato ou uma versão do Minecraft não permitidos.");
        return (manifest.Name, manifest.Version);
    }

    public static void Export(GameInstanceInfo instance, string archivePath)
    {
        if (!GameVersionCatalog.IsSupported(instance.Version)) throw new InvalidOperationException("Versão de instância não permitida.");
        var temporaryPath = archivePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                var manifest = new InstanceArchiveManifest(1, instance.Name, instance.Version, ReadLoader(instance.Directory));
                using (var stream = archive.CreateEntry("sapvp-instance.json", CompressionLevel.Optimal).Open())
                    JsonSerializer.Serialize(stream, manifest, Json);

                var entries = 0;
                long bytes = 0;
                foreach (var folder in IncludedDirectories)
                {
                    var source = Path.Combine(instance.Directory, folder);
                    if (!Directory.Exists(source)) continue;
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var info = new FileInfo(file);
                        if (++entries > MaximumEntries || (bytes += info.Length) > MaximumUncompressedBytes) throw new InvalidDataException("A instância excede o limite de tamanho para exportação (20 GB/100 mil arquivos).");
                        archive.CreateEntryFromFile(file, Path.GetRelativePath(instance.Directory, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    }
                }
                foreach (var fileName in IncludedRootFiles)
                {
                    var file = Path.Combine(instance.Directory, fileName);
                    if (!File.Exists(file)) continue;
                    var info = new FileInfo(file);
                    if (++entries > MaximumEntries || (bytes += info.Length) > MaximumUncompressedBytes) throw new InvalidDataException("A instância excede o limite de tamanho para exportação.");
                    archive.CreateEntryFromFile(file, fileName, CompressionLevel.Optimal);
                }
            }
            File.Move(temporaryPath, archivePath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public static GameInstanceInfo Duplicate(GameInstanceInfo source, string name)
    {
        var duplicate = GameInstanceManager.CreateNamedInstance(source.Version, name, ReadLoader(source.Directory));
        try
        {
            CopyUserFiles(source.Directory, duplicate.Directory);
            return GameInstanceManager.GetInstances().First(instance => instance.Directory == duplicate.Directory);
        }
        catch
        {
            if (Directory.Exists(duplicate.Directory)) Directory.Delete(duplicate.Directory, true);
            throw;
        }
    }

    public static GameInstanceInfo Import(string archivePath, string name)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.GetEntry("sapvp-instance.json") ?? throw new InvalidDataException("O pacote não contém o manifesto SAPVP-AI.");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<InstanceArchiveManifest>(manifestStream, Json)
            ?? throw new InvalidDataException("O manifesto da instância está vazio ou inválido.");
        if (manifest.Format != 1 || !GameVersionCatalog.IsSupported(manifest.Version)) throw new InvalidDataException("O pacote usa um formato ou uma versão do Minecraft não permitidos.");
        ValidateArchive(archive);

        var instance = GameInstanceManager.CreateNamedInstance(manifest.Version, name, manifest.Loader);
        try
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals("sapvp-instance.json", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name)) continue;
                var relative = entry.FullName.Replace('\\', '/');
                var destination = ResolveSafePath(instance.Directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = File.Create(destination);
                input.CopyTo(output);
            }
            return GameInstanceManager.GetInstances().First(item => item.Directory == instance.Directory);
        }
        catch
        {
            if (Directory.Exists(instance.Directory)) Directory.Delete(instance.Directory, true);
            throw;
        }
    }

    private static void CopyUserFiles(string source, string destination)
    {
        foreach (var folder in IncludedDirectories)
        {
            var from = Path.Combine(source, folder);
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var to = ResolveSafePath(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        }
        foreach (var fileName in IncludedRootFiles)
        {
            var from = Path.Combine(source, fileName);
            if (File.Exists(from)) File.Copy(from, Path.Combine(destination, fileName), true);
        }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("O pacote contém arquivos demais.");
        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Equals("sapvp-instance.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            ResolveArchiveRelativePath(entry.FullName);
            var normalized = entry.FullName.Replace('\\', '/');
            var first = normalized.Split('/')[0];
            if (!IncludedDirectories.Contains(first, StringComparer.OrdinalIgnoreCase) && !IncludedRootFiles.Contains(normalized))
                throw new InvalidDataException($"O pacote contém um caminho não permitido: {entry.FullName}");
            if (entry.Length > MaximumUncompressedBytes || (bytes += entry.Length) > MaximumUncompressedBytes) throw new InvalidDataException("O pacote excede o limite de 20 GB descompactados.");
        }
    }

    private static string ResolveSafePath(string root, string relative)
    {
        ResolveArchiveRelativePath(relative);
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O pacote tentou gravar fora da pasta da instância.");
        return destination;
    }

    private static void ResolveArchiveRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split(['/', '\\']).Any(part => part is ".." or "."))
            throw new InvalidDataException("O pacote contém um caminho inválido.");
    }

    private static string? ReadLoader(string instancePath)
    {
        try
        {
            var metadata = Path.Combine(instancePath, "sapvp-instance.json");
            if (File.Exists(metadata))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                if (document.RootElement.TryGetProperty("loader", out var loader)) return loader.GetString();
            }
            var pack = Path.Combine(instancePath, "modrinth-pack.json");
            if (File.Exists(pack))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(pack));
                if (document.RootElement.TryGetProperty("loaderVersion", out var version)) return version.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return null;
    }

    private sealed record InstanceArchiveManifest(int Format, string Name, string Version, string? Loader);
}
