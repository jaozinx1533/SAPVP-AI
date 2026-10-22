using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MinecraftProjectLauncher.Services;

public sealed class ModrinthService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.modrinth.com/v2/"), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftProjectLauncher/0.1.0 (open-source launcher)");
        return client;
    }

    public async Task<ModrinthSearchPage> SearchModsAsync(string query, string gameVersion, string loader, string sort = "downloads", int offset = 0, int limit = 20, string? category = null)
    {
        var loaderId = NormalizeLoader(loader);
        var facetGroups = new List<string[]>
        {
            new[] { "project_type:mod" },
            new[] { $"versions:{gameVersion}" },
            new[] { $"categories:{loaderId}" }
        };
        if (!string.IsNullOrWhiteSpace(category) && category.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            facetGroups.Add(new[] { $"categories:{category.ToLowerInvariant()}" });
        var facets = JsonSerializer.Serialize(facetGroups);
        var index = sort is "downloads" or "updated" or "newest" or "relevance" or "follows" ? sort : "downloads";
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var parameters = $"facets={Uri.EscapeDataString(facets)}&limit={limit}&offset={offset}&index={index}";
        if (!string.IsNullOrWhiteSpace(query)) parameters += $"&query={Uri.EscapeDataString(query)}";
        var response = await Http.GetAsync($"search?{parameters}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(Json) ?? new SearchResponse();
        var hits = result.Hits.Where(hit => !ModSafetyPolicy.IsBlockedProject(hit.ProjectId, hit.Slug, hit.Title)).Select(hit => new ModrinthMod(
            hit.ProjectId, hit.Title, hit.Description,
            $"{hit.Author}  ·  {hit.Downloads:N0} downloads  ·  {loader}  ·  Minecraft {gameVersion}", hit.IconUrl, hit.Downloads, hit.Slug))
            .ToArray();
        return new ModrinthSearchPage(hits, result.TotalHits, result.Offset, result.Limit);
    }

    public async Task<ModrinthSearchPage> SearchResourcePacksAsync(string query, string gameVersion, string sort = "downloads", int offset = 0, int limit = 20, string? category = null)
        => await SearchProjectsByTypeAsync("resourcepack", "Resource pack", query, gameVersion, sort, offset, limit, category);

    public async Task<ModrinthSearchPage> SearchShadersAsync(string query, string gameVersion, string sort = "downloads", int offset = 0, int limit = 20, string? category = null)
        => await SearchProjectsByTypeAsync("shader", "Shader", query, gameVersion, sort, offset, limit, category);

    private async Task<ModrinthSearchPage> SearchProjectsByTypeAsync(string projectType, string displayType, string query, string gameVersion, string sort, int offset, int limit, string? category)
    {
        var facetGroups = new List<string[]> { new[] { $"project_type:{projectType}" }, new[] { $"versions:{gameVersion}" } };
        if (!string.IsNullOrWhiteSpace(category) && category.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            facetGroups.Add(new[] { $"categories:{category.ToLowerInvariant()}" });
        var facets = JsonSerializer.Serialize(facetGroups);
        var index = sort is "downloads" or "updated" or "newest" or "relevance" or "follows" ? sort : "downloads";
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var parameters = $"facets={Uri.EscapeDataString(facets)}&limit={limit}&offset={offset}&index={index}";
        if (!string.IsNullOrWhiteSpace(query)) parameters += $"&query={Uri.EscapeDataString(query)}";
        using var response = await Http.GetAsync($"search?{parameters}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(Json) ?? new SearchResponse();
        var hits = result.Hits.Select(hit => new ModrinthMod(hit.ProjectId, hit.Title, hit.Description,
            $"{hit.Author}  ·  {hit.Downloads:N0} downloads  ·  {displayType}  ·  Minecraft {gameVersion}", hit.IconUrl, hit.Downloads, hit.Slug)).ToArray();
        return new ModrinthSearchPage(hits, result.TotalHits, result.Offset, result.Limit);
    }

    public async Task InstallResourcePackAsync(ModrinthMod project, string gameVersion, string targetDirectory)
        => await InstallProjectArchiveAsync(project, gameVersion, targetDirectory, "resourcepack", "resource pack");

    public async Task InstallShaderAsync(ModrinthMod project, string gameVersion, string targetDirectory)
        => await InstallProjectArchiveAsync(project, gameVersion, targetDirectory, "shader", "shader pack");

    private async Task InstallProjectArchiveAsync(ModrinthMod project, string gameVersion, string targetDirectory, string expectedType, string displayType)
    {
        var projectResponse = await Http.GetAsync($"project/{Uri.EscapeDataString(project.ProjectId)}");
        projectResponse.EnsureSuccessStatusCode();
        var details = await projectResponse.Content.ReadFromJsonAsync<ProjectDetailsResponse>(Json)
            ?? throw new InvalidDataException("Não foi possível validar este resource pack.");
        if (!string.Equals(details.ProjectType, expectedType, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Este projeto não é um {displayType}.");

        var games = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }));
        using var versionsResponse = await Http.GetAsync($"project/{Uri.EscapeDataString(project.ProjectId)}/version?game_versions={games}");
        versionsResponse.EnsureSuccessStatusCode();
        var versions = await versionsResponse.Content.ReadFromJsonAsync<List<ModrinthVersion>>(Json) ?? [];
        var version = versions.Where(item => item.Status == "listed" && item.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => DateTimeOffset.TryParse(item.DatePublished, out var date) ? date : DateTimeOffset.MinValue)
            .FirstOrDefault() ?? throw new InvalidOperationException($"Não há versão compatível com Minecraft {gameVersion}.");
        var file = version.Files.FirstOrDefault(item => item.Primary) ?? version.Files.FirstOrDefault()
            ?? throw new InvalidDataException("O Modrinth não informou um arquivo para esta versão.");
        var fileName = Path.GetFileName(file.FileName);
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"O arquivo publicado não é um .zip válido para {displayType}.");
        var uri = new Uri(file.Url);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("O Modrinth retornou um endereço de download inesperado.");
        Directory.CreateDirectory(targetDirectory);
        var destination = Path.Combine(targetDirectory, fileName);
        if (File.Exists(destination)) throw new IOException($"O arquivo {fileName} já existe na pasta de resource packs.");
        var temporary = destination + ".download";
        try
        {
            using var download = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            download.EnsureSuccessStatusCode();
            await using (var output = File.Create(temporary)) await download.Content.CopyToAsync(output);
            await VerifyHashAsync(temporary, file.Hashes);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ModrinthInstallEstimate> EstimateResourcePackInstallAsync(string projectId, string gameVersion)
        => await EstimateProjectArchiveInstallAsync(projectId, gameVersion, "resource pack");

    public async Task<ModrinthInstallEstimate> EstimateShaderInstallAsync(string projectId, string gameVersion)
        => await EstimateProjectArchiveInstallAsync(projectId, gameVersion, "shader pack");

    private async Task<ModrinthInstallEstimate> EstimateProjectArchiveInstallAsync(string projectId, string gameVersion, string displayType)
    {
        var games = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }));
        using var response = await Http.GetAsync($"project/{Uri.EscapeDataString(projectId)}/version?game_versions={games}");
        response.EnsureSuccessStatusCode();
        var versions = await response.Content.ReadFromJsonAsync<List<ModrinthVersion>>(Json) ?? [];
        var file = versions.Where(version => version.Status == "listed" && version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            .SelectMany(version => version.Files).OrderByDescending(item => item.Primary).FirstOrDefault()
            ?? throw new InvalidOperationException($"O Modrinth não informou o tamanho do {displayType}.");
        return new ModrinthInstallEstimate(Math.Max(0, file.Size), 1);
    }

    public async Task<ModrinthProjectDetails> GetProjectDetailsAsync(string projectId)
    {
        var response = await Http.GetAsync($"project/{Uri.EscapeDataString(projectId)}");
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDetailsResponse>(Json)
            ?? throw new InvalidOperationException("Não foi possível ler os detalhes deste mod.");
        return new ModrinthProjectDetails(project.Id, project.Title, project.Description, project.Body ?? "", project.IconUrl,
            project.Categories.Concat(project.AdditionalCategories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            project.Downloads, project.Followers, project.SourceUrl, project.WikiUrl, project.IssuesUrl, project.Slug,
            project.Gallery.Where(image => !string.IsNullOrWhiteSpace(image.Url)).Select(image => new ModrinthGalleryImage(image.Url, image.Title, image.Description)).ToArray());
    }

    public async Task<string?> GetProjectIconUrlAsync(string projectId)
    {
        var response = await Http.GetAsync($"project/{Uri.EscapeDataString(projectId)}");
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDetailsResponse>(Json);
        return project?.IconUrl;
    }

    public async Task<string?> GetProjectIconFromJarAsync(string jarPath)
    {
        await using var file = File.OpenRead(jarPath);
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(file)).ToLowerInvariant();
        using var response = await Http.GetAsync($"version_file/{sha1}?algorithm=sha1");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var fileInfo = await response.Content.ReadFromJsonAsync<VersionFileLookupResponse>(Json);
        return string.IsNullOrWhiteSpace(fileInfo?.ProjectId) ? null : await GetProjectIconUrlAsync(fileInfo.ProjectId);
    }

    public async Task<IReadOnlyList<ModrinthReleaseOption>> GetSupportedReleasesAsync(string projectId, string loader, IEnumerable<string> allowedGameVersions)
    {
        var allowed = allowedGameVersions.Where(GameVersionCatalog.IsSupported).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (allowed.Length == 0) return [];
        var loaderId = NormalizeLoader(loader);
        var games = Uri.EscapeDataString(JsonSerializer.Serialize(allowed));
        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderId }));
        var response = await Http.GetAsync($"project/{Uri.EscapeDataString(projectId)}/version?game_versions={games}&loaders={loaders}&include_changelog=true");
        response.EnsureSuccessStatusCode();
        var versions = await response.Content.ReadFromJsonAsync<List<ModrinthVersion>>(Json) ?? [];
        return versions
            .Where(version => version.Status == "listed" && version.Loaders.Contains(loaderId, StringComparer.OrdinalIgnoreCase))
            .Select(version => new ModrinthReleaseOption(
                version.Id,
                version.VersionNumber,
                version.VersionType,
                version.GameVersions.Where(game => allowed.Contains(game, StringComparer.OrdinalIgnoreCase)).ToArray(),
                version.DatePublished,
                version.Changelog))
            .Where(option => option.GameVersions.Count > 0)
            .OrderByDescending(option => DateTimeOffset.TryParse(option.DatePublished, out var date) ? date : DateTimeOffset.MinValue)
            .ToArray();
    }

    public async Task InstallModAsync(ModrinthMod mod, string gameVersion, string loader, string targetDirectory, string? selectedVersionId = null)
    {
        if (ModSafetyPolicy.IsBlockedProject(mod.ProjectId, mod.Slug, mod.Title))
            throw new InvalidOperationException($"{mod.Title} está bloqueado pela política de mods do launcher para proteger o PvP.");
        var normalizedLoader = NormalizeLoader(loader);
        var rootVersion = string.IsNullOrWhiteSpace(selectedVersionId)
            ? await FindCompatibleVersionAsync(mod.ProjectId, gameVersion, normalizedLoader)
            : await GetVersionAsync(selectedVersionId);
        if (rootVersion.Status != "listed" || !rootVersion.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) ||
            !rootVersion.Loaders.Contains(normalizedLoader, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A versão escolhida não é compatível com Minecraft {gameVersion} e {loader}.");
        Directory.CreateDirectory(targetDirectory);
        var installedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mod.ProjectId };
        var visitedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await InstallVersionTreeAsync(rootVersion, gameVersion, normalizedLoader, targetDirectory, installedProjects, visitedVersions);
        var managed = LoadInstalledMods(targetDirectory).ToList();
        var file = rootVersion.Files.FirstOrDefault(f => f.Primary) ?? rootVersion.Files.FirstOrDefault(f => f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (file is not null)
        {
            managed.RemoveAll(item => string.Equals(item.ProjectId, mod.ProjectId, StringComparison.OrdinalIgnoreCase));
            managed.Add(new ManagedMod(mod.ProjectId, mod.Title, rootVersion.Id, rootVersion.VersionNumber, SafeFileName(file.FileName), gameVersion, loader, mod.IconUrl));
            SaveInstalledMods(targetDirectory, managed);
        }
    }

    public async Task<ModrinthInstallEstimate> EstimateModInstallAsync(string projectId, string gameVersion, string loader, string? selectedVersionId = null)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var fileCount = 0;
        async Task Visit(ModrinthVersion version)
        {
            if (!visited.Add(version.Id)) return;
            var file = version.Files.FirstOrDefault(item => item.Primary) ?? version.Files.FirstOrDefault(item => item.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            if (file is not null) { bytes += Math.Max(0, file.Size); fileCount++; }
            foreach (var dependency in version.Dependencies.Where(item => item.DependencyType == "required"))
            {
                ModrinthVersion? dependencyVersion = null;
                if (!string.IsNullOrWhiteSpace(dependency.VersionId)) dependencyVersion = await GetVersionAsync(dependency.VersionId);
                else if (!string.IsNullOrWhiteSpace(dependency.ProjectId)) dependencyVersion = await FindCompatibleVersionAsync(dependency.ProjectId, gameVersion, NormalizeLoader(loader));
                if (dependencyVersion is not null) await Visit(dependencyVersion);
            }
        }
        var root = string.IsNullOrWhiteSpace(selectedVersionId)
            ? await FindCompatibleVersionAsync(projectId, gameVersion, NormalizeLoader(loader))
            : await GetVersionAsync(selectedVersionId);
        await Visit(root);
        return new ModrinthInstallEstimate(bytes, fileCount);
    }

    public async Task<int> UpdateManagedModsAsync(string gameVersion, string loader, string targetDirectory)
    {
        var managed = LoadInstalledMods(targetDirectory);
        var updatedCount = 0;
        foreach (var installed in managed.ToArray())
        {
            var latest = await FindCompatibleVersionAsync(installed.ProjectId, gameVersion, NormalizeLoader(loader));
            if (string.Equals(latest.Id, installed.VersionId, StringComparison.OrdinalIgnoreCase)) continue;
            var project = new ModrinthMod(installed.ProjectId, installed.Name, "", "", null, 0, "");
            var oldFile = Path.Combine(targetDirectory, installed.FileName);
            await InstallModAsync(project, gameVersion, loader, targetDirectory);
            var fresh = LoadInstalledMods(targetDirectory).FirstOrDefault(item => item.ProjectId == installed.ProjectId);
            if (fresh is not null && !string.Equals(fresh.FileName, installed.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile)) File.Delete(oldFile);
            updatedCount++;
        }
        return updatedCount;
    }

    public async Task<string?> UpdateManagedModAsync(ManagedMod installed, string gameVersion, string loader, string targetDirectory, string? selectedVersionId = null)
    {
        if (string.IsNullOrWhiteSpace(installed.ProjectId))
            throw new InvalidOperationException("Este arquivo não foi instalado pelo Modrinth dentro do launcher e não pode ser atualizado automaticamente.");
        var latest = string.IsNullOrWhiteSpace(selectedVersionId)
            ? await FindCompatibleVersionAsync(installed.ProjectId, gameVersion, NormalizeLoader(loader))
            : await GetVersionAsync(selectedVersionId);
        if (latest.Status != "listed" || !latest.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) || !latest.Loaders.Contains(NormalizeLoader(loader), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A versão selecionada não é compatível com Minecraft {gameVersion} / {loader}.");
        if (string.Equals(latest.Id, installed.VersionId, StringComparison.OrdinalIgnoreCase)) return null;
        var oldFile = Path.Combine(targetDirectory, SafeFileName(installed.FileName));
        var mod = new ModrinthMod(installed.ProjectId, installed.Name, "", "", null, 0, "");
        await InstallModAsync(mod, gameVersion, loader, targetDirectory, latest.Id);
        var current = LoadInstalledMods(targetDirectory).FirstOrDefault(item => item.ProjectId == installed.ProjectId);
        if (current is not null && !string.Equals(current.FileName, installed.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile))
            File.Delete(oldFile);
        return current?.VersionNumber;
    }

    public static void RemoveInstalledMod(ManagedMod mod, string targetDirectory)
    {
        var fileName = SafeFileName(mod.FileName);
        var path = Path.Combine(targetDirectory, fileName);
        if (File.Exists(path)) File.Delete(path);
        if (string.IsNullOrWhiteSpace(mod.ProjectId)) return;
        var managed = LoadInstalledMods(targetDirectory).Where(item => !string.Equals(item.ProjectId, mod.ProjectId, StringComparison.OrdinalIgnoreCase)).ToArray();
        SaveInstalledMods(targetDirectory, managed);
    }

    public async Task<IReadOnlyList<ManagedModUpdate>> CheckManagedModUpdatesAsync(string gameVersion, string loader, string targetDirectory)
    {
        var updates = new List<ManagedModUpdate>();
        foreach (var installed in LoadInstalledMods(targetDirectory))
        {
            var latest = await FindCompatibleVersionAsync(installed.ProjectId, gameVersion, NormalizeLoader(loader));
            if (!string.Equals(latest.Id, installed.VersionId, StringComparison.OrdinalIgnoreCase))
                updates.Add(new ManagedModUpdate(installed.Name, installed.VersionNumber, latest.VersionNumber));
        }
        return updates;
    }

    public async Task<IReadOnlyList<string>> CheckInstalledModConflictsAsync(string gameVersion, string loader, string targetDirectory)
    {
        var safetyWarnings = ModSafetyPolicy.FindBlockedJars(targetDirectory).ToList();
        var warnings = new List<string>();
        var fabricMods = new List<(string File, string Id, HashSet<string> Depends, HashSet<string> Breaks)>();
        if (Directory.Exists(targetDirectory))
        {
            foreach (var jarPath in Directory.EnumerateFiles(targetDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var jar = ZipFile.OpenRead(jarPath);
                    var metadataEntry = jar.GetEntry("fabric.mod.json");
                    if (metadataEntry is null) continue;
                    using var stream = metadataEntry.Open();
                    using var metadata = await JsonDocument.ParseAsync(stream);
                    var root = metadata.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) || string.IsNullOrWhiteSpace(idElement.GetString())) continue;
                    static HashSet<string> ReadIds(JsonElement obj, string key) => obj.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Object
                        ? values.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    fabricMods.Add((Path.GetFileName(jarPath), idElement.GetString()!, ReadIds(root, "depends"), ReadIds(root, "breaks")));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException) { warnings.Add($"{Path.GetFileName(jarPath)}: não foi possível ler fabric.mod.json para validar dependências."); }
            }
        }
        var installedIds = fabricMods.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraft", "fabricloader", "java", "fabric" };
        foreach (var mod in fabricMods)
        {
            foreach (var dependency in mod.Depends.Where(id => !builtIns.Contains(id) && !installedIds.Contains(id)))
                warnings.Add($"{mod.File} declara dependência Fabric ausente: {dependency}.");
            foreach (var conflict in mod.Breaks.Where(installedIds.Contains))
                warnings.Add($"{mod.File} declara incompatibilidade Fabric com {conflict}.");
        }
        var installed = LoadInstalledMods(targetDirectory)
            .Where(mod => !string.IsNullOrWhiteSpace(mod.ProjectId) && !string.IsNullOrWhiteSpace(mod.VersionId))
            .ToArray();
        if (installed.Length == 0) return safetyWarnings.Concat(warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var projectIds = installed.Select(mod => mod.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = installed.GroupBy(mod => mod.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(4, 4);
        var checks = installed.Select(async mod =>
        {
            await gate.WaitAsync();
            try
            {
                return (mod, version: await GetVersionAsync(mod.VersionId));
            }
            finally { gate.Release(); }
        });
        var checkedVersions = await Task.WhenAll(checks);
        foreach (var (mod, version) in checkedVersions)
        {
            if (!version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) ||
                !version.Loaders.Contains(NormalizeLoader(loader), StringComparer.OrdinalIgnoreCase))
                warnings.Add($"{mod.Name}: a versão registrada pode não ser compatível com Minecraft {gameVersion} / {loader}.");

            foreach (var dependency in version.Dependencies.Where(item => item.DependencyType == "incompatible" &&
                         !string.IsNullOrWhiteSpace(item.ProjectId) && projectIds.Contains(item.ProjectId)))
            {
                var otherName = names.TryGetValue(dependency.ProjectId!, out var foundName) ? foundName : dependency.ProjectId!;
                warnings.Add($"{mod.Name} declara incompatibilidade com {otherName}.");
            }
        }
        return safetyWarnings.Concat(warnings).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<ManagedMod> LoadInstalledMods(string targetDirectory)
    {
        var statePath = Path.Combine(targetDirectory, "modrinth-installed.json");
        if (!File.Exists(statePath)) return [];
        try { return JsonSerializer.Deserialize<List<ManagedMod>>(File.ReadAllText(statePath), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void SaveInstalledMods(string targetDirectory, IReadOnlyList<ManagedMod> mods)
    {
        var statePath = Path.Combine(targetDirectory, "modrinth-installed.json");
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(mods, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, statePath, true);
    }

    private async Task InstallVersionTreeAsync(ModrinthVersion version, string gameVersion, string loader, string targetDirectory,
        HashSet<string> installedProjects, HashSet<string> visitedVersions)
    {
        if (!visitedVersions.Add(version.Id)) return;
        if (!string.IsNullOrWhiteSpace(version.ProjectId))
        {
            using var projectResponse = await Http.GetAsync($"project/{Uri.EscapeDataString(version.ProjectId)}");
            projectResponse.EnsureSuccessStatusCode();
            var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDetailsResponse>(Json);
            if (project is not null && ModSafetyPolicy.IsBlockedProject(project.Id, project.Slug, project.Title))
                throw new InvalidOperationException($"{project.Title} está bloqueado pela política de mods do launcher para proteger o PvP.");
        }

        foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required"))
        {
            if (string.IsNullOrWhiteSpace(dependency.ProjectId) || !installedProjects.Add(dependency.ProjectId)) continue;
            var depVersion = !string.IsNullOrWhiteSpace(dependency.VersionId)
                ? await GetVersionAsync(dependency.VersionId)
                : await FindCompatibleVersionAsync(dependency.ProjectId, gameVersion, loader);
            if (!depVersion.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) ||
                !depVersion.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"A dependência obrigatória {dependency.ProjectId} não tem versão compatível com Minecraft {gameVersion} e {loader}.");
            await InstallVersionTreeAsync(depVersion, gameVersion, loader, targetDirectory, installedProjects, visitedVersions);
        }

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault(f => f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (file is null || !file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A versão {version.VersionNumber} não oferece um arquivo JAR instalável.");
        var uri = new Uri(file.Url);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("O Modrinth retornou um endereço de download inesperado.");

        var destination = Path.Combine(targetDirectory, SafeFileName(file.FileName));
        var temporary = destination + ".download";
        using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(temporary);
            await source.CopyToAsync(output);
        }
        try
        {
            await VerifyHashAsync(temporary, file.Hashes);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<ModrinthVersion> FindCompatibleVersionAsync(string projectId, string gameVersion, string loader)
    {
        var url = $"project/{Uri.EscapeDataString(projectId)}/version?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}";
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var versions = await response.Content.ReadFromJsonAsync<List<ModrinthVersion>>(Json) ?? [];
        return versions.FirstOrDefault(v => v.Status == "listed" && v.VersionType == "release")
            ?? versions.FirstOrDefault(v => v.Status == "listed")
            ?? throw new InvalidOperationException($"Não foi encontrada versão compatível para {gameVersion} ({loader}).");
    }

    private static async Task<ModrinthVersion> GetVersionAsync(string versionId)
    {
        var response = await Http.GetAsync($"version/{Uri.EscapeDataString(versionId)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModrinthVersion>(Json)
            ?? throw new InvalidOperationException("Resposta de versão inválida do Modrinth.");
    }

    private static async Task VerifyHashAsync(string path, Dictionary<string, string> hashes)
    {
        string? expected = null;
        string? actual = null;
        if (hashes.TryGetValue("sha512", out expected))
        {
            await using var stream = File.OpenRead(path);
            actual = Convert.ToHexString(await SHA512.HashDataAsync(stream)).ToLowerInvariant();
        }
        else if (hashes.TryGetValue("sha1", out expected))
        {
            await using var stream = File.OpenRead(path);
            actual = Convert.ToHexString(await SHA1.HashDataAsync(stream)).ToLowerInvariant();
        }
        if (expected is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual!)))
            throw new InvalidDataException("A verificação de integridade do arquivo falhou.");
    }

    private static string NormalizeLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => "neoforge",
        "forge" => "forge",
        "quilt" => "quilt",
        _ => "fabric"
    };

    private static string SafeFileName(string name)
    {
        var safe = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
        if (!safe.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Extensão de arquivo inválida.");
        return safe;
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("hits")] public List<SearchHit> Hits { get; init; } = [];
        [JsonPropertyName("total_hits")] public int TotalHits { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
        [JsonPropertyName("limit")] public int Limit { get; init; }
    }
    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")] public string ProjectId { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("description")] public string Description { get; init; } = "";
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("author")] public string Author { get; init; } = "";
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
    }
    private sealed class ProjectDetailsResponse
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("description")] public string Description { get; init; } = "";
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("categories")] public List<string> Categories { get; init; } = [];
        [JsonPropertyName("additional_categories")] public List<string> AdditionalCategories { get; init; } = [];
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
        [JsonPropertyName("followers")] public long Followers { get; init; }
        [JsonPropertyName("source_url")] public string? SourceUrl { get; init; }
        [JsonPropertyName("wiki_url")] public string? WikiUrl { get; init; }
        [JsonPropertyName("issues_url")] public string? IssuesUrl { get; init; }
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("project_type")] public string ProjectType { get; init; } = "";
        [JsonPropertyName("gallery")] public List<ProjectGalleryResponse> Gallery { get; init; } = [];
    }
    private sealed class VersionFileLookupResponse
    {
        [JsonPropertyName("project_id")] public string ProjectId { get; init; } = "";
    }
    private sealed class ProjectGalleryResponse
    {
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }
    private sealed class ModrinthVersion
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("project_id")] public string ProjectId { get; init; } = "";
        [JsonPropertyName("version_number")] public string VersionNumber { get; init; } = "";
        [JsonPropertyName("date_published")] public string DatePublished { get; init; } = "";
        [JsonPropertyName("changelog")] public string? Changelog { get; init; }
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("version_type")] public string VersionType { get; init; } = "";
        [JsonPropertyName("game_versions")] public List<string> GameVersions { get; init; } = [];
        [JsonPropertyName("loaders")] public List<string> Loaders { get; init; } = [];
        [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; init; } = [];
        [JsonPropertyName("files")] public List<ModrinthFile> Files { get; init; } = [];
    }
    private sealed class ModrinthDependency
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
        [JsonPropertyName("version_id")] public string? VersionId { get; init; }
        [JsonPropertyName("dependency_type")] public string DependencyType { get; init; } = "";
    }
    private sealed class ModrinthFile
    {
        [JsonPropertyName("filename")] public string FileName { get; init; } = "";
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("primary")] public bool Primary { get; init; }
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("size")] public long Size { get; init; }
    }
}

public sealed record ModrinthMod(string ProjectId, string Title, string Description, string Detail, string? IconUrl, long Downloads, string Slug);
public sealed record ModrinthSearchPage(IReadOnlyList<ModrinthMod> Hits, int TotalHits, int Offset, int Limit);
public sealed record ModrinthProjectDetails(string ProjectId, string Title, string Description, string Body, string? IconUrl, IReadOnlyList<string> Categories, long Downloads, long Followers, string? SourceUrl, string? WikiUrl, string? IssuesUrl, string Slug, IReadOnlyList<ModrinthGalleryImage> Gallery);
public sealed record ModrinthGalleryImage(string Url, string? Title, string? Description);
public sealed record ModrinthReleaseOption(string VersionId, string VersionNumber, string VersionType, IReadOnlyList<string> GameVersions, string DatePublished, string? Changelog)
{
    public string Label => $"{VersionNumber} · {string.Join(", ", GameVersions)} · {VersionType}";
    public override string ToString() => Label;
}
public sealed record ManagedMod(string ProjectId, string Name, string VersionId, string VersionNumber, string FileName, string GameVersion, string Loader, string? IconUrl = null)
{
    public bool IsManaged => !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(VersionId);
}
public sealed record ManagedModUpdate(string Name, string InstalledVersion, string LatestCompatibleVersion);
public sealed record ModrinthInstallEstimate(long Bytes, int Files);
