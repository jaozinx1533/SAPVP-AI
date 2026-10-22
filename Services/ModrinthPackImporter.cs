using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using MinecraftProjectLauncher;

namespace MinecraftProjectLauncher.Services;

public sealed class ModrinthPackImporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<ModrinthPackManifest> ReadManifestAsync(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var indexEntry = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Este arquivo não é um modpack Modrinth válido.");
        await using var indexStream = indexEntry.Open();
        using var document = await JsonDocument.ParseAsync(indexStream);
        return ParseManifest(document.RootElement, archivePath);
    }

    public async Task<ModrinthPackImportResult> ImportAsync(string archivePath, string instancePath, string expectedGameVersion,
        IProgress<ModrinthPackImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!GameVersionCatalog.IsSupported(expectedGameVersion)) throw new InvalidOperationException("A versão escolhida não é permitida neste launcher.");
        using var archive = ZipFile.OpenRead(archivePath);
        var indexEntry = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Este arquivo não é um modpack Modrinth válido.");
        using var indexStream = indexEntry.Open();
        using var document = await JsonDocument.ParseAsync(indexStream);
        var root = document.RootElement;
        var manifest = ParseManifest(root, archivePath);
        var gameVersion = manifest.GameVersion;
        if (!string.Equals(gameVersion, expectedGameVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Este modpack é para Minecraft {gameVersion}; a versão selecionada é {expectedGameVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.FabricLoaderVersion))
            throw new InvalidOperationException("Este modpack não declara o Fabric Loader. Por enquanto, o launcher importa modpacks Fabric.");

        Directory.CreateDirectory(instancePath);
        var modsDirectory = Path.Combine(instancePath, "mods");
        Directory.CreateDirectory(modsDirectory);
        var installedCount = 0;
        var skippedCount = 0;
        var fileEntries = root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array ? fileArray.EnumerateArray().ToArray() : [];
        var eligibleCount = fileEntries.Count(entry => !(entry.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var clientEnv) && clientEnv.GetString() == "unsupported"));
        var currentFile = 0;
        if (root.TryGetProperty("files", out _))
        {
            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var clientEnv) && clientEnv.GetString() == "unsupported") { skippedCount++; continue; }
                var relativePath = entry.GetProperty("path").GetString() ?? throw new InvalidDataException("Caminho de arquivo ausente no modpack.");
                var targetPath = ResolveSafePath(instancePath, relativePath);
                var urls = entry.GetProperty("downloads").EnumerateArray().Select(item => item.GetString()).Where(url => !string.IsNullOrWhiteSpace(url)).ToArray();
                if (urls.Length == 0) throw new InvalidDataException($"O modpack não fornece endereço para {relativePath}.");
                var hashes = entry.TryGetProperty("hashes", out var hashObject) ? hashObject : default;
                var downloaded = false;
                foreach (var url in urls)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                        !IsApprovedDownloadHost(uri.Host)) continue;
                    var temp = targetPath + ".download";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        response.EnsureSuccessStatusCode();
                        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                        await using (var output = File.Create(temp))
                        {
                            await source.CopyToAsync(output, cancellationToken);
                        }
                        await VerifyHashAsync(temp, hashes);
                        File.Move(temp, targetPath, true);
                        downloaded = true;
                        installedCount++;
                        currentFile++;
                        progress?.Report(new ModrinthPackImportProgress(currentFile, eligibleCount, relativePath));
                        break;
                    }
                    catch (OperationCanceledException) { if (File.Exists(temp)) File.Delete(temp); throw; }
                    catch (HttpRequestException) { if (File.Exists(temp)) File.Delete(temp); }
                    catch { if (File.Exists(temp)) File.Delete(temp); throw; }
                }
                if (!downloaded) throw new InvalidOperationException($"Não foi possível baixar com segurança o arquivo {relativePath}. O .mrpack deve usar HTTPS em um servidor permitido pelo formato Modrinth (Modrinth, GitHub ou GitLab).");
            }
        }

        var overrideFiles = 0;
        var configFiles = 0;
        var optionsFileExtracted = false;
        // Apply shared overrides first, then client-overrides (which take precedence per the mrpack format).
        foreach (var entry in archive.Entries
                     .Select(entry => (Entry: entry, Relative: NormalizeArchiveEntryPath(entry.FullName)))
                     .Where(item => TryGetOverridePath(item.Relative, out _, out _))
                     .OrderBy(item => item.Relative.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            var relative = entry.Relative;
            if (!TryGetOverridePath(relative, out var overridePath, out _)) continue;
            if (string.IsNullOrWhiteSpace(overridePath)) continue;
            var path = ResolveSafePath(instancePath, overridePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Entry.Open();
            await using var output = File.Create(path);
            cancellationToken.ThrowIfCancellationRequested();
            await input.CopyToAsync(output, cancellationToken);
            overrideFiles++;
            var firstSegment = overridePath.Split('/', '\\')[0];
            if (firstSegment.Equals("config", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals("defaultconfigs", StringComparison.OrdinalIgnoreCase)) configFiles++;
            if (overridePath.Equals("options.txt", StringComparison.OrdinalIgnoreCase)) optionsFileExtracted = true;
        }

        var name = manifest.Name;
        var loaderVersion = manifest.FabricLoaderVersion;
        var metadata = new { name, gameVersion, loader = "fabric", loaderVersion, importedFrom = "Modrinth", importedAt = DateTimeOffset.UtcNow };
        await File.WriteAllTextAsync(Path.Combine(instancePath, "modrinth-pack.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        return new ModrinthPackImportResult(name, gameVersion, loaderVersion, installedCount, overrideFiles, skippedCount, configFiles, optionsFileExtracted);
    }

    private static string NormalizeArchiveEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    private static bool TryGetOverridePath(string archivePath, out string relativePath, out string source)
    {
        relativePath = string.Empty;
        source = string.Empty;
        if (archivePath.EndsWith('/')) return false;

        string? prefix = null;
        if (archivePath.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)) prefix = "client-overrides/";
        else if (archivePath.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) prefix = "overrides/";
        else if (archivePath.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase)) prefix = ".minecraft/";
        else if (archivePath.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase)) prefix = "minecraft/";
        else if (IsKnownInstanceRootEntry(archivePath)) prefix = string.Empty;
        if (prefix is null) return false;

        source = prefix;
        relativePath = archivePath[prefix.Length..];
        if (relativePath.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase)) relativePath = relativePath[".minecraft/".Length..];
        else if (relativePath.StartsWith("minecraft/", StringComparison.OrdinalIgnoreCase)) relativePath = relativePath["minecraft/".Length..];
        if ((prefix == ".minecraft/" || prefix == "minecraft/") && !IsKnownInstanceRootEntry(relativePath)) return false;
        return !string.IsNullOrWhiteSpace(relativePath);
    }

    private static bool IsKnownInstanceRootEntry(string path)
    {
        var firstSegment = path.Split('/')[0];
        if (path.Contains('/')) return firstSegment.Equals("config", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("defaultconfigs", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("shaderpacks", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("saves", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("screenshots", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("mods", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("kubejs", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("scripts", StringComparison.OrdinalIgnoreCase);
        return path.Equals("options.txt", StringComparison.OrdinalIgnoreCase)
            || path.Equals("optionsof.txt", StringComparison.OrdinalIgnoreCase)
            || path.Equals("servers.dat", StringComparison.OrdinalIgnoreCase)
            || path.Equals("servers.dat_old", StringComparison.OrdinalIgnoreCase);
    }

    private static ModrinthPackManifest ParseManifest(JsonElement root, string archivePath)
    {
        if (!root.TryGetProperty("formatVersion", out var format) || format.GetInt32() != 1)
            throw new InvalidDataException("A versão do formato deste modpack não é suportada.");
        if (!root.TryGetProperty("game", out var game) || game.GetString() != "minecraft")
            throw new InvalidDataException("Este pacote não é para Minecraft.");
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object ||
            !dependencies.TryGetProperty("minecraft", out var minecraft))
            throw new InvalidDataException("O modpack não informa a versão do Minecraft.");
        var gameVersion = minecraft.GetString() ?? "";
        var fabricVersion = dependencies.TryGetProperty("fabric-loader", out var fabric) ? fabric.GetString() : null;

        // Accept the legacy modLoaders array as well, while following the current mrpack spec above.
        if (string.IsNullOrWhiteSpace(fabricVersion) && dependencies.TryGetProperty("modLoaders", out var loaderArray) && loaderArray.ValueKind == JsonValueKind.Array)
        {
            fabricVersion = loaderArray.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .FirstOrDefault(id => id?.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) == true)?["fabric-loader-".Length..];
        }

        var name = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        var fileCount = root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array ? files.GetArrayLength() : 0;
        return new ModrinthPackManifest(
            string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(archivePath) : name,
            gameVersion,
            fabricVersion?.Trim() ?? "", fileCount);
    }

    private static string ResolveSafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("O modpack contém um caminho absoluto inválido.");
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O modpack tentou gravar fora da instância.");
        return destination;
    }

    private static bool IsApprovedDownloadHost(string host) =>
        host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase);

    private static async Task VerifyHashAsync(string path, JsonElement hashes)
    {
        if (hashes.ValueKind != JsonValueKind.Object) throw new InvalidDataException("O arquivo do modpack não possui hash para validação.");
        if (hashes.TryGetProperty("sha512", out var sha512))
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA512.HashDataAsync(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha512.GetString() ?? ""), Convert.FromHexString(actual))) throw new InvalidDataException("Hash SHA-512 inválido no download do modpack.");
            return;
        }
        if (hashes.TryGetProperty("sha1", out var sha1))
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha1.GetString() ?? ""), Convert.FromHexString(actual))) throw new InvalidDataException("Hash SHA-1 inválido no download do modpack.");
            return;
        }
        throw new InvalidDataException("O arquivo do modpack não possui SHA-1 ou SHA-512.");
    }
}

public sealed record ModrinthPackManifest(string Name, string GameVersion, string FabricLoaderVersion, int FileCount);
public sealed record ModrinthPackImportProgress(int Completed, int Total, string CurrentFile);
public sealed record ModrinthPackImportResult(string Name, string GameVersion, string LoaderVersion, int FilesInstalled, int OverridesExtracted, int FilesSkipped, int ConfigFilesExtracted, bool OptionsFileExtracted);
