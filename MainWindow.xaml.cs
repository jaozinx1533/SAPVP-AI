using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using MinecraftProjectLauncher.Services;
using System.Net.Http;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;

namespace MinecraftProjectLauncher;

public partial class MainWindow : Window
{
    private bool _launchInProgress;
    private bool _trayIconAdded;
    private const int TrayCallbackMessage = 0x8000 + 47;
    private const uint TrayAdd = 0;
    private const uint TrayDelete = 2;
    private const uint TrayMessageFlag = 1;
    private const uint TrayIconFlag = 2;
    private const uint TrayTipFlag = 4;
    private const uint TrayVersion4 = 4;
    private const int TrayDoubleClick = 0x0203;
    private string? _runningVersion;
    private string? _runningInstancePath;
    private string? _selectedManagedInstancePath;
    private long? _gameStartedUnix;
    private readonly DiscordPresenceService _discordPresence = new();
    private readonly DispatcherTimer _discordPresenceTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly ModrinthService _modrinth = new();
    private readonly LauncherUpdateService _launcherUpdateService = new();
    private IReadOnlyList<ModrinthMod> _searchResults = [];
    private int _modSearchPage;
    private int _modSearchTotal;
    private int _resourcePackSearchPage;
    private int _resourcePackSearchTotal;
    private int _shaderSearchPage;
    private int _shaderSearchTotal;
    private const int ModSearchPageSize = 20;
    private ModrinthMod? _activeModDetails;
    private ModrinthProjectDetails? _loadedModDetails;
    private ManagedMod? _managedModToUpdate;
    private bool _openingManagedModUpdate;
    private readonly Dictionary<string, string?> _modIconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool IsCustomInstance(string path) => path.Contains(Path.Combine("instances", "custom") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string GetCurrentInstancePath(string version)
    {
        if (!string.IsNullOrWhiteSpace(_selectedManagedInstancePath))
        {
            var selected = GameInstanceManager.GetInstances().FirstOrDefault(instance => string.Equals(instance.Directory, _selectedManagedInstancePath, StringComparison.OrdinalIgnoreCase));
            if (selected?.Version == version) return selected.Directory;
        }
        return GameInstanceManager.GetInstancePath(version);
    }
    private bool _loadingModDetails;
    private readonly LauncherPreferences _preferences = LauncherExtrasStore.LoadPreferences();
    private readonly DispatcherTimer _newsTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private List<NewsCard> _newsCards = [];
    private int? _lastNewsCount;
    private bool _loadingProfiles;
    private bool _loadingRecentInstances;
    private string? _preferredInstallVersion;
    private string? _preferredInstallInstancePath;
    private bool _loadingSettings;
    private bool _refreshingNews;
    private DateTime _lastReadNewsCleanup = DateTime.MinValue;
    private static readonly HttpClient NewsHttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private sealed record HomeRecentInstanceChoice(string? Directory, string? Version, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record ManagedInstanceAsset(string Name, string Description, string FullPath, bool IsDirectory);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(HandleTrayMessage);
        };
        VersionSelector.ItemsSource = new[] { "Escolha sua versão" }.Concat(GameVersionCatalog.SupportedVersions).ToList();
        VersionSelector.SelectedIndex = 0;
        LibraryGameVersionBox.ItemsSource = GameVersionCatalog.SupportedVersions;
        LibraryGameVersionBox.SelectedItem = GameVersionCatalog.DefaultVersion;
        ModLoaderBox.ItemsSource = new[] { "Fabric" };
        ModLoaderBox.SelectedIndex = 0;
        ModSortBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "Mais baixados", Tag = "downloads" },
            new ComboBoxItem { Content = "Relevância", Tag = "relevance" },
            new ComboBoxItem { Content = "Novidades", Tag = "newest" },
            new ComboBoxItem { Content = "Atualizados", Tag = "updated" }
        };
        ModSortBox.SelectedIndex = 0;
        ResourcePackGameVersionBox.ItemsSource = GameVersionCatalog.SupportedVersions;
        ResourcePackGameVersionBox.SelectedItem = GameVersionCatalog.DefaultVersion;
        ResourcePackSortBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "Mais baixados", Tag = "downloads" },
            new ComboBoxItem { Content = "Relevância", Tag = "relevance" },
            new ComboBoxItem { Content = "Novidades", Tag = "newest" },
            new ComboBoxItem { Content = "Atualizados", Tag = "updated" }
        };
        ResourcePackSortBox.SelectedIndex = 0;
        ShaderGameVersionBox.ItemsSource = GameVersionCatalog.SupportedVersions;
        ShaderGameVersionBox.SelectedItem = GameVersionCatalog.DefaultVersion;
        ShaderSortBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "Mais baixados", Tag = "downloads" },
            new ComboBoxItem { Content = "Relevância", Tag = "relevance" },
            new ComboBoxItem { Content = "Novidades", Tag = "newest" },
            new ComboBoxItem { Content = "Atualizados", Tag = "updated" }
        };
        ShaderSortBox.SelectedIndex = 0;
        VersionSelector.SelectionChanged += (_, _) =>
        {
            _selectedManagedInstancePath = null;
            if (!_loadingRecentInstances && HomeRecentInstanceSelector.SelectedItem is HomeRecentInstanceChoice recent && recent.Version != VersionSelector.SelectedItem as string)
                HomeRecentInstanceSelector.SelectedIndex = 0;
            RefreshModrinthInstance();
            RefreshHomeDashboard();
        };
        Loaded += async (_, _) => await SearchModsAsync(showErrors: false);
        _discordPresenceTimer.Tick += async (_, _) => await RefreshDiscordPresenceAsync();
        Closed += async (_, _) =>
        {
            RemoveTrayIcon();
            _discordPresenceTimer.Stop();
            try { await _discordPresence.DisposeAsync(); } catch { }
        };
        RefreshHomeRecentInstances();
        RefreshModrinthInstance();
        RefreshHomeDashboard();
        Loaded += (_, _) => InitializeExtras();
        _newsTimer.Tick += async (_, _) => await RefreshNewsAsync();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private IntPtr HandleTrayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == TrayCallbackMessage && (lParam.ToInt32() & 0xFFFF) == TrayDoubleClick)
        {
            Dispatcher.BeginInvoke(new Action(RestoreLauncherWindow));
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void AddTrayIcon()
    {
        if (_trayIconAdded || !IsLoaded) return;
        var handle = new WindowInteropHelper(this).Handle;
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = 1,
            uFlags = TrayMessageFlag | TrayIconFlag | TrayTipFlag,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            szTip = "SAPVP-AI — clique duas vezes para abrir",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        _trayIconAdded = Shell_NotifyIcon(TrayAdd, ref data);
        if (_trayIconAdded)
        {
            data.uTimeoutOrVersion = TrayVersion4;
            Shell_NotifyIcon(4, ref data);
        }
    }

    private void RemoveTrayIcon()
    {
        if (!_trayIconAdded) return;
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = new WindowInteropHelper(this).Handle,
            uID = 1,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
        Shell_NotifyIcon(TrayDelete, ref data);
        _trayIconAdded = false;
    }

    private void RestoreLauncherWindow()
    {
        RemoveTrayIcon();
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    private async void LaunchMinecraft_Click(object sender, RoutedEventArgs e)
    {
        if (HomeRecentInstanceSelector.SelectedItem is HomeRecentInstanceChoice { Directory: { Length: > 0 } path })
        {
            var recent = GameInstanceManager.GetInstances().FirstOrDefault(item => string.Equals(item.Directory, path, StringComparison.OrdinalIgnoreCase));
            if (recent is not null && VersionSelector.SelectedItem as string == recent.Version)
            {
                await LaunchMinecraftAsync(recent);
                return;
            }
        }
        await LaunchMinecraftAsync(null);
    }

    private async void LaunchInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameInstanceInfo instance)
            await LaunchMinecraftAsync(instance);
    }

    private async Task LaunchMinecraftAsync(GameInstanceInfo? requestedInstance)
    {
        if (_launchInProgress)
        {
            return;
        }

        var selectedVersion = requestedInstance?.Version ?? VersionSelector.SelectedItem as string;
        if (selectedVersion is null || !GameVersionCatalog.IsSupported(selectedVersion))
        {
            MessageBox.Show("Selecione uma das versões permitidas para continuar.", "Versão não permitida",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var effectiveMemoryMb = _preferences.MemoryMb;

        var chosenInstancePath = requestedInstance?.Directory ?? GameInstanceManager.GetInstancePath(selectedVersion);
        var blockedMods = ModSafetyPolicy.FindBlockedJars(Path.Combine(chosenInstancePath, "mods"));
        if (blockedMods.Count > 0)
        {
            MessageBox.Show(
                "A inicialização foi bloqueada porque a instância contém mods identificados como cheats/clientes PvP proibidos:\n\n" + string.Join("\n", blockedMods) + "\n\nRemova esses arquivos da pasta mods para continuar.",
                "Mods bloqueados", MessageBoxButton.OK, MessageBoxImage.Stop);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_preferences.JavaPath) && !File.Exists(_preferences.JavaPath))
        {
            MessageBox.Show("O executável Java escolhido não foi encontrado. Selecione outro arquivo ou use a opção Automático nas configurações.", "Java não encontrado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_preferences.MinimumMemoryMb > effectiveMemoryMb)
        {
            MessageBox.Show("A memória inicial não pode ser maior que a memória máxima. Ajuste esse valor nas configurações.", "Memória inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LaunchButton.IsEnabled = false;
        LaunchProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Preparando para iniciar o Minecraft…";

        SetLaunchBusy(true, "Conectando à conta Microsoft...");
        Process? gameProcess = null;
        var sessionStarted = DateTime.Now;

        try
        {
            var loginHandler = JELoginHandlerBuilder.BuildDefault();
            var selectedAccount = _preferences.MicrosoftAccountUuid is { Length: > 0 } selectedUuid
                ? loginHandler.AccountManager.GetAccounts().OfType<JEGameAccount>().FirstOrDefault(account => string.Equals(account.Profile?.UUID, selectedUuid, StringComparison.OrdinalIgnoreCase))
                : null;
            var session = selectedAccount is not null
                ? await loginHandler.Authenticate(selectedAccount)
                : await loginHandler.Authenticate();
            _preferences.MicrosoftAccountName = session.Username;
            _preferences.MicrosoftAccountUuid = session.UUID;
            LauncherExtrasStore.SavePreferences(_preferences);
            RefreshMicrosoftAccounts(session.UUID);

            SetLaunchBusy(true, $"Preparando Minecraft Java {selectedVersion}. O primeiro download pode demorar.");

            var instancePath = requestedInstance?.Directory ?? GameInstanceManager.EnsureInstance(selectedVersion);
            Directory.CreateDirectory(instancePath);
            Directory.CreateDirectory(Path.Combine(instancePath, "mods"));
            try
            {
                if (InstanceBackupService.CreateAutomaticIfDue(selectedVersion, instancePath) is { } backupPath)
                    StatusText.Text = $"Backup automático salvo antes de iniciar: {Path.GetFileName(backupPath)}";
            }
            catch (Exception backupError)
            {
                StatusText.Text = $"Aviso: não foi possível criar backup automático ({backupError.Message}). Continuando a inicialização.";
            }
            var minecraftLauncher = new MinecraftLauncher(new MinecraftPath(instancePath));
            var knownVersions = await minecraftLauncher.GetAllVersionsAsync();
            if (!knownVersions.Any(version => string.Equals(version.Name, selectedVersion, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"A versão {selectedVersion} não foi encontrada no manifesto oficial do Minecraft.");
            }

            var launchVersion = selectedVersion;
            var modsPath = Path.Combine(instancePath, "mods");
            var hasMods = Directory.EnumerateFiles(modsPath, "*.jar", System.IO.SearchOption.TopDirectoryOnly).Any();
            var fabricRequested = hasMods || IsFabricInstance(instancePath);
            if (fabricRequested)
            {
                var fabricInstaller = new FabricInstaller(new HttpClient());
                var requestedLoader = GetImportedFabricLoaderVersion(instancePath);
                launchVersion = string.IsNullOrWhiteSpace(requestedLoader)
                    ? await fabricInstaller.Install(selectedVersion, new MinecraftPath(instancePath))
                    : await fabricInstaller.Install(selectedVersion, requestedLoader, new MinecraftPath(instancePath));
            }

            var launchOptions = new MLaunchOption
            {
                Session = session,
                MaximumRamMb = effectiveMemoryMb,
                MinimumRamMb = Math.Min(_preferences.MinimumMemoryMb, effectiveMemoryMb),
                FullScreen = _preferences.FullScreen,
                ScreenWidth = 1280,
                ScreenHeight = 720
            };
            if (!string.IsNullOrWhiteSpace(_preferences.JavaPath)) launchOptions.JavaPath = _preferences.JavaPath;
            if (!string.IsNullOrWhiteSpace(_preferences.ExtraJvmArguments))
                launchOptions.ExtraJvmArguments = [MArgument.FromCommandLine(_preferences.ExtraJvmArguments)];
            gameProcess = await minecraftLauncher.InstallAndBuildProcessAsync(launchVersion, launchOptions);

            if (!gameProcess.Start())
            {
                throw new InvalidOperationException("O processo do Minecraft não iniciou.");
            }

            RecordLaunchedInstance(instancePath, selectedVersion);

            _runningVersion = selectedVersion;
            _runningInstancePath = instancePath;
            _gameStartedUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
            _ = RefreshDiscordPresenceAsync();
            StatusText.Text = "Minecraft está aberto. O SAPVP-AI está na área de ícones ocultos.";
            ShowInTaskbar = false;
            AddTrayIcon();
            Hide();

            await gameProcess.WaitForExitAsync();
            LauncherExtrasStore.AddSession(new SessionReport(selectedVersion, fabricRequested ? "Fabric" : "Vanilla", sessionStarted, DateTime.Now, gameProcess.ExitCode, gameProcess.ExitCode == 0 ? "Concluída" : "Encerrada com erro"));

            _runningVersion = null;
            _runningInstancePath = null;
            _gameStartedUnix = null;
            _ = RefreshDiscordPresenceAsync();
            RestoreLauncherWindow();
            StatusText.Text = "Minecraft foi fechado. Você pode iniciar outra sessão.";
            RefreshReports();
            RefreshInstanceList();
            RefreshHomeDashboard();
        }
        catch (Exception exception)
        {
            if (gameProcess is not null && sessionStarted < DateTime.Now)
                LauncherExtrasStore.AddSession(new SessionReport(selectedVersion, "Minecraft", sessionStarted, DateTime.Now, gameProcess.HasExited ? gameProcess.ExitCode : -1, "Falha/encerramento inesperado") { Diagnosis = CrashDiagnosticService.Diagnose(selectedVersion, gameProcess.HasExited ? gameProcess.ExitCode : -1, exception.Message) });
            _runningVersion = null;
            _runningInstancePath = null;
            _gameStartedUnix = null;
            _ = RefreshDiscordPresenceAsync();
            RestoreLauncherWindow();
            StatusText.Text = "Não foi possível iniciar o jogo.";
            RefreshHomeDashboard();
            MessageBox.Show(
                $"O launcher não conseguiu iniciar o Minecraft.\n\n{exception.Message}",
                "Falha ao iniciar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            gameProcess?.Dispose();
            SetLaunchBusy(false, StatusText.Text);
        }
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(HomePage, HomeNavButton);
        RefreshHomeDashboard();
    }

    private void HomeRecentInstanceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRecentInstances || HomeRecentInstanceSelector.SelectedItem is not HomeRecentInstanceChoice { Directory: { Length: > 0 } path, Version: { Length: > 0 } version }) return;
        if (!GameVersionCatalog.IsSupported(version)) return;
        if (VersionSelector.SelectedItem as string != version) VersionSelector.SelectedItem = version;
        _selectedManagedInstancePath = path;
        RefreshModrinthInstance();
        RefreshHomeDashboard();
    }

    private void RefreshHomeRecentInstances(string? selectedPath = null)
    {
        if (HomeRecentInstanceSelector is null) return;
        _preferences.RecentInstances ??= [];
        var instances = GameInstanceManager.GetInstances();
        var validRecent = _preferences.RecentInstances
            .OrderByDescending(item => item.LastLaunchedAt)
            .Select(recent => (Recent: recent, Instance: instances.FirstOrDefault(instance =>
                string.Equals(instance.Directory, recent.Directory, StringComparison.OrdinalIgnoreCase) && instance.Version == recent.Version)))
            .Where(item => item.Instance is not null)
            .Select(item => new HomeRecentInstanceChoice(item.Instance!.Directory, item.Instance.Version, $"{item.Instance.Name} · Minecraft {item.Instance.Version}"))
            .DistinctBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingPaths = instances.Select(item => item.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedRecentEntries = _preferences.RecentInstances.RemoveAll(item => !existingPaths.Contains(item.Directory));
        if (removedRecentEntries > 0) LauncherExtrasStore.SavePreferences(_preferences);
        var recentPaths = validRecent.Select(item => item.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableInstances = validRecent.Concat(instances
                .Where(instance => !recentPaths.Contains(instance.Directory))
                .Select(instance => new HomeRecentInstanceChoice(instance.Directory, instance.Version, $"{instance.Name} · Minecraft {instance.Version}")))
            .Take(50)
            .ToList();

        var requestedPath = selectedPath ?? (HomeRecentInstanceSelector.SelectedItem as HomeRecentInstanceChoice)?.Directory;
        var currentPath = availableInstances.FirstOrDefault(item => string.Equals(item.Directory, requestedPath, StringComparison.OrdinalIgnoreCase))?.Directory
            ?? availableInstances.FirstOrDefault()?.Directory;
        HomeRecentInstancePanel.Visibility = availableInstances.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _loadingRecentInstances = true;
        try
        {
            HomeRecentInstanceSelector.DisplayMemberPath = nameof(HomeRecentInstanceChoice.Label);
            System.Windows.Controls.TextSearch.SetTextPath(HomeRecentInstanceSelector, nameof(HomeRecentInstanceChoice.Label));
            HomeRecentInstanceSelector.ItemsSource = new[] { new HomeRecentInstanceChoice(null, null, "Selecione uma instância") }.Concat(availableInstances).ToArray();
            HomeRecentInstanceSelector.SelectedItem = availableInstances.FirstOrDefault(item => string.Equals(item.Directory, currentPath, StringComparison.OrdinalIgnoreCase));
            if (HomeRecentInstanceSelector.SelectedItem is null) HomeRecentInstanceSelector.SelectedIndex = 0;

            if (HomeRecentInstanceSelector.SelectedItem is HomeRecentInstanceChoice { Directory: { Length: > 0 } path, Version: { Length: > 0 } version })
            {
                if (VersionSelector.SelectedItem as string != version) VersionSelector.SelectedItem = version;
                _selectedManagedInstancePath = path;
            }
            else
            {
                _selectedManagedInstancePath = null;
            }
        }
        finally { _loadingRecentInstances = false; }
    }

    private void RecordLaunchedInstance(string instancePath, string version)
    {
        try
        {
            var fullPath = Path.GetFullPath(instancePath);
            var instance = GameInstanceManager.GetInstances().FirstOrDefault(item => string.Equals(item.Directory, fullPath, StringComparison.OrdinalIgnoreCase));
            var displayName = instance?.Name ?? $"Minecraft {version}";
            _preferences.RecentInstances ??= [];
            _preferences.RecentInstances.RemoveAll(item => string.Equals(item.Directory, fullPath, StringComparison.OrdinalIgnoreCase));
            _preferences.RecentInstances.Insert(0, new RecentInstanceLaunch(fullPath, version, displayName, DateTime.Now));
            _preferences.RecentInstances = _preferences.RecentInstances.Take(20).ToList();
            LauncherExtrasStore.SavePreferences(_preferences);
            RefreshHomeRecentInstances(fullPath);
        }
        catch (Exception ex) { StatusText.Text = $"Jogo iniciado; não foi possível salvar no histórico da página Início: {ex.Message}"; }
    }

    private void RefreshHomeDashboard()
    {
        if (HomeAccountStatus is null) return;
        HomeAccountStatus.Text = HomeAccountSelector.SelectedItem is MicrosoftAccountChoice selectedAccount
            ? $"Conta selecionada: {selectedAccount.Username}"
            : string.IsNullOrWhiteSpace(_preferences.MicrosoftAccountName) ? "Nenhuma conta conectada. Adicione uma conta Microsoft original." : $"Conta selecionada: {_preferences.MicrosoftAccountName}";

        HomeNewsStatus.Text = _lastNewsCount is int count
            ? count == 0 ? "Nenhuma novidade nova" : $"{count} novidade(s) no mural"
            : "Feed sincronizado automaticamente";
        if (VersionSelector?.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version))
        {
            HomeInstanceStatus.Text = "Escolha uma versão permitida para continuar";
            HomeProfileStatus.Text = "Selecione uma versão para ver o perfil";
            HomeContentStatus.Text = "Nenhum perfil selecionado";
            return;
        }

        var recentSelection = HomeRecentInstanceSelector.SelectedItem as HomeRecentInstanceChoice;
        var instancePath = recentSelection?.Directory ?? GameInstanceManager.GetInstancePath(version);
        var instance = GameInstanceManager.GetInstances().FirstOrDefault(item => string.Equals(item.Directory, instancePath, StringComparison.OrdinalIgnoreCase));
        var installState = instance?.IsInstalled == true ? "instalado" : Directory.Exists(instancePath) ? "pronto para instalar" : "ainda não baixado";
        HomeInstanceStatus.Text = recentSelection?.Directory is not null && instance is not null
            ? $"{instance.Name} · Minecraft {version} · {installState}"
            : $"Minecraft {version} · {installState}";
        var profile = ModpackProfileStore.GetActiveProfile(version);
        HomeProfileStatus.Text = recentSelection?.Directory is not null && IsCustomInstance(instancePath)
            ? "Instância independente · mods e configurações próprios"
            : $"Perfil de mods: {profile.Name}";

        var modsDirectory = Path.Combine(instancePath, "mods");
        var modCount = Directory.Exists(modsDirectory) ? Directory.GetFiles(modsDirectory, "*.jar", System.IO.SearchOption.TopDirectoryOnly).Length : 0;
        HomeContentStatus.Text = $"{modCount} mod(s) no perfil ativo";
    }

    private sealed record MicrosoftAccountChoice(string Uuid, string Username, JEGameAccount Account);

    private void RefreshMicrosoftAccounts(string? selectUuid = null)
    {
        var accounts = JELoginHandlerBuilder.BuildDefault().AccountManager.GetAccounts()
            .OfType<JEGameAccount>()
            .Where(account => !string.IsNullOrWhiteSpace(account.Profile?.UUID) && !string.IsNullOrWhiteSpace(account.Profile?.Username))
            .Select(account => new MicrosoftAccountChoice(account.Profile!.UUID!, account.Profile.Username!, account))
            .OrderBy(account => account.Username, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var desiredUuid = selectUuid ?? _preferences.MicrosoftAccountUuid;
        HomeAccountSelector.ItemsSource = accounts;
        HomeAccountSelector.SelectedItem = accounts.FirstOrDefault(account => string.Equals(account.Uuid, desiredUuid, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault();
        if (HomeAccountSelector.SelectedItem is MicrosoftAccountChoice selected)
        {
            _preferences.MicrosoftAccountUuid = selected.Uuid;
            _preferences.MicrosoftAccountName = selected.Username;
            AccountStatus.Text = $"Conta selecionada: {selected.Username} · {accounts.Count} conta(s) salva(s)";
        }
        else
        {
            _preferences.MicrosoftAccountUuid = null;
            AccountStatus.Text = "Nenhuma conta Microsoft conectada";
        }
        LauncherExtrasStore.SavePreferences(_preferences);
        RefreshHomeDashboard();
    }

    private void HomeAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomeAccountSelector.SelectedItem is not MicrosoftAccountChoice account) return;
        _preferences.MicrosoftAccountUuid = account.Uuid;
        _preferences.MicrosoftAccountName = account.Username;
        LauncherExtrasStore.SavePreferences(_preferences);
        SkinPreviewImage.Source = null;
        CapePreviewImage.Source = null;
        SkinPreviewLabel.Text = "Atualize para ver a skin desta conta";
        CapePreviewLabel.Text = "Atualize para ver a capa desta conta";
        OwnedCapeBox.ItemsSource = null;
        AccountStatus.Text = $"Conta selecionada: {account.Username}";
        RefreshHomeDashboard();
        if (SettingsPage.Visibility == Visibility.Visible) _ = RefreshAppearanceAsync(showErrors: false);
    }

    private void InstancesNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ModrinthPage, InstancesNavButton);
        LibraryTitle.Text = "Instâncias";
        LibrarySubtitle.Text = "Gerencie suas versões instaladas, mods e arquivos separados.";
        ModDetailsView.Visibility = Visibility.Collapsed;
        LibraryTabs.Visibility = Visibility.Collapsed;
        LibraryInstancesView.Visibility = Visibility.Visible;
        RefreshInstanceList();
        RefreshModrinthInstance();
    }

    private async void ModsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(ModrinthPage, ModsNavButton);
        LibraryTitle.Text = "Biblioteca";
        LibrarySubtitle.Text = "Explore mods e resource packs do Modrinth e organize suas versões.";
        ModDetailsView.Visibility = Visibility.Collapsed;
        LibraryTabs.Visibility = Visibility.Visible;
        LibraryInstancesView.Visibility = Visibility.Collapsed;
        LibraryTabs.SelectedItem = ExploreModsTab;
        RefreshModrinthInstance();
        await SearchModsAsync(showErrors: false);
    }

    private async void LibraryVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && LibraryGameVersionBox.SelectedItem is not null)
            await SearchModsAsync(showErrors: false);
    }

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await SearchModsAsync();
    private async void ModSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ModSortBox.SelectedItem is not null) await SearchModsAsync(showErrors: false);
    }

    private async void LibraryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() => _ = RefreshDiscordPresenceAsync()), DispatcherPriority.Background);
        if (IsLoaded && ReferenceEquals(LibraryTabs.SelectedItem, ResourcePacksTab) && ResourcePackResultsList.ItemsSource is null)
            await SearchResourcePacksAsync(showErrors: false);
        if (IsLoaded && ReferenceEquals(LibraryTabs.SelectedItem, ShadersTab) && ShaderResultsList.ItemsSource is null)
            await SearchShadersAsync(showErrors: false);
    }
    private async void ModCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ModCategoryBox.SelectedItem is not null) await SearchModsAsync(showErrors: false);
    }
    private async void ModSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await SearchModsAsync();
    }

    private async Task SearchModsAsync(bool showErrors = true, bool keepCurrentPage = false)
    {
        if (ModLoaderBox.SelectedItem is not string loader || LibraryGameVersionBox.SelectedItem is not string gameVersion) return;
        if (!keepCurrentPage) _modSearchPage = 0;
        ModSearchButton.IsEnabled = false;
        ModResultsLabel.Text = $"Buscando página {_modSearchPage + 1}…";
        PreviousModPageButton.IsEnabled = false;
        NextModPageButton.IsEnabled = false;
        try
        {
            var sort = (ModSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "downloads";
            var category = (ModCategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var page = await _modrinth.SearchModsAsync(ModSearchBox.Text.Trim(), gameVersion, loader, sort, _modSearchPage * ModSearchPageSize, ModSearchPageSize, category);
            _searchResults = page.Hits;
            _modSearchTotal = page.TotalHits;
            ApplyLibraryFilter();
            var firstResult = page.TotalHits == 0 ? 0 : page.Offset + 1;
            var lastResult = page.Offset + _searchResults.Count;
            ModResultsLabel.Text = page.TotalHits == 0
                ? $"Nenhum resultado · Minecraft {gameVersion} · {loader}"
                : $"{firstResult:N0}–{lastResult:N0} de {page.TotalHits:N0} · Minecraft {gameVersion} · {loader}";
            ModPageLabel.Text = page.TotalHits == 0 ? "Página 0 de 0" : $"Página {_modSearchPage + 1} de {Math.Ceiling(page.TotalHits / (double)ModSearchPageSize):N0}";
            PreviousModPageButton.IsEnabled = _modSearchPage > 0;
            NextModPageButton.IsEnabled = page.Offset + _searchResults.Count < _modSearchTotal;
        }
        catch (Exception ex)
        {
            ModResultsLabel.Text = "Não foi possível carregar o catálogo.";
            ModPageLabel.Text = "Falha ao carregar esta página";
            PreviousModPageButton.IsEnabled = _modSearchPage > 0;
            NextModPageButton.IsEnabled = (_modSearchPage + 1) * ModSearchPageSize < _modSearchTotal;
            if (showErrors) MessageBox.Show(ex.Message, "Modrinth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ModSearchButton.IsEnabled = true; }
    }

    private async void ResourcePackSearch_Click(object sender, RoutedEventArgs e) => await SearchResourcePacksAsync();
    private async void ResourcePackSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await SearchResourcePacksAsync();
    }
    private async void ResourcePackCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ResourcePackCategoryBox.SelectedItem is not null) await SearchResourcePacksAsync(showErrors: false);
    }

    private async void ResourcePackVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ResourcePackGameVersionBox.SelectedItem is not null && ResourcePackResultsList.ItemsSource is not null)
            await SearchResourcePacksAsync(showErrors: false);
    }
    private async void ResourcePackSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ResourcePackSortBox.SelectedItem is not null) await SearchResourcePacksAsync(showErrors: false);
    }

    private async Task SearchResourcePacksAsync(bool showErrors = true, bool keepCurrentPage = false)
    {
        if (ResourcePackGameVersionBox.SelectedItem is not string gameVersion) return;
        if (!keepCurrentPage) _resourcePackSearchPage = 0;
        ResourcePackSearchButton.IsEnabled = false;
        ResourcePackResultsLabel.Text = $"Buscando página {_resourcePackSearchPage + 1}…";
        PreviousResourcePackPageButton.IsEnabled = false;
        NextResourcePackPageButton.IsEnabled = false;
        try
        {
            var sort = (ResourcePackSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "downloads";
            var category = (ResourcePackCategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var page = await _modrinth.SearchResourcePacksAsync(ResourcePackSearchBox.Text.Trim(), gameVersion, sort,
                _resourcePackSearchPage * ModSearchPageSize, ModSearchPageSize, category);
            ResourcePackResultsList.ItemsSource = page.Hits;
            _resourcePackSearchTotal = page.TotalHits;
            var first = page.TotalHits == 0 ? 0 : page.Offset + 1;
            var last = page.Offset + page.Hits.Count;
            ResourcePackResultsLabel.Text = page.TotalHits == 0 ? $"Nenhum resultado · Minecraft {gameVersion}" : $"{first:N0}–{last:N0} de {page.TotalHits:N0} · Minecraft {gameVersion}";
            ResourcePackPageLabel.Text = page.TotalHits == 0 ? "Página 0 de 0" : $"Página {_resourcePackSearchPage + 1} de {Math.Ceiling(page.TotalHits / (double)ModSearchPageSize):N0}";
            PreviousResourcePackPageButton.IsEnabled = _resourcePackSearchPage > 0;
            NextResourcePackPageButton.IsEnabled = page.Offset + page.Hits.Count < _resourcePackSearchTotal;
        }
        catch (Exception ex)
        {
            ResourcePackResultsLabel.Text = "Não foi possível carregar o catálogo.";
            ResourcePackPageLabel.Text = "Falha ao carregar esta página";
            if (showErrors) MessageBox.Show(ex.Message, "Modrinth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ResourcePackSearchButton.IsEnabled = true; }
    }

    private async void PreviousResourcePackPage_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcePackSearchPage <= 0) return;
        _resourcePackSearchPage--;
        await SearchResourcePacksAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void NextResourcePackPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_resourcePackSearchPage + 1) * ModSearchPageSize >= _resourcePackSearchTotal) return;
        _resourcePackSearchPage++;
        await SearchResourcePacksAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void ShaderSearch_Click(object sender, RoutedEventArgs e) => await SearchShadersAsync();
    private async void ShaderSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) await SearchShadersAsync();
    }
    private async void ShaderCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ShaderCategoryBox.SelectedItem is not null) await SearchShadersAsync(showErrors: false);
    }
    private async void ShaderVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ShaderGameVersionBox.SelectedItem is not null && ShaderResultsList.ItemsSource is not null)
            await SearchShadersAsync(showErrors: false);
    }
    private async void ShaderSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ShaderSortBox.SelectedItem is not null) await SearchShadersAsync(showErrors: false);
    }

    private async Task SearchShadersAsync(bool showErrors = true, bool keepCurrentPage = false)
    {
        if (ShaderGameVersionBox.SelectedItem is not string gameVersion) return;
        if (!keepCurrentPage) _shaderSearchPage = 0;
        ShaderSearchButton.IsEnabled = false;
        ShaderResultsLabel.Text = $"Buscando página {_shaderSearchPage + 1}…";
        PreviousShaderPageButton.IsEnabled = false;
        NextShaderPageButton.IsEnabled = false;
        try
        {
            var sort = (ShaderSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "downloads";
            var category = (ShaderCategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var page = await _modrinth.SearchShadersAsync(ShaderSearchBox.Text.Trim(), gameVersion, sort,
                _shaderSearchPage * ModSearchPageSize, ModSearchPageSize, category);
            ShaderResultsList.ItemsSource = page.Hits;
            _shaderSearchTotal = page.TotalHits;
            var first = page.TotalHits == 0 ? 0 : page.Offset + 1;
            var last = page.Offset + page.Hits.Count;
            ShaderResultsLabel.Text = page.TotalHits == 0 ? $"Nenhum resultado · Minecraft {gameVersion}" : $"{first:N0}–{last:N0} de {page.TotalHits:N0} · Minecraft {gameVersion}";
            ShaderPageLabel.Text = page.TotalHits == 0 ? "Página 0 de 0" : $"Página {_shaderSearchPage + 1} de {Math.Ceiling(page.TotalHits / (double)ModSearchPageSize):N0}";
            PreviousShaderPageButton.IsEnabled = _shaderSearchPage > 0;
            NextShaderPageButton.IsEnabled = page.Offset + page.Hits.Count < _shaderSearchTotal;
        }
        catch (Exception ex)
        {
            ShaderResultsLabel.Text = "Não foi possível carregar o catálogo.";
            ShaderPageLabel.Text = "Falha ao carregar esta página";
            if (showErrors) MessageBox.Show(ex.Message, "Modrinth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShaderSearchButton.IsEnabled = true; }
    }

    private async void PreviousShaderPage_Click(object sender, RoutedEventArgs e)
    {
        if (_shaderSearchPage <= 0) return;
        _shaderSearchPage--;
        await SearchShadersAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void NextShaderPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_shaderSearchPage + 1) * ModSearchPageSize >= _shaderSearchTotal) return;
        _shaderSearchPage++;
        await SearchShadersAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void InstallShader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthMod shader || ShaderGameVersionBox.SelectedItem is not string selectedVersion) return;
        var version = ChooseModInstallVersion(shader.Title, [selectedVersion]);
        if (version is null) return;
        try { if (!await ConfirmDownloadSpaceAsync(version, await _modrinth.EstimateShaderInstallAsync(shader.ProjectId, version))) return; }
        catch (Exception ex) { MessageBox.Show($"Não foi possível estimar o download: {ex.Message}\n\nA instalação foi cancelada.", "Estimativa de espaço", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var selectedInstancePath = GetCurrentInstancePath(version);
        if (!CreatePreChangeBackup(version, selectedInstancePath)) return;
        try
        {
            ShaderResultsLabel.Text = $"Instalando {shader.Title} em Minecraft {version}…";
            await _modrinth.InstallShaderAsync(shader, version, Path.Combine(selectedInstancePath, "shaderpacks"));
            VersionSelector.SelectedItem = version;
            RefreshInstanceList();
            RefreshHomeRecentInstances(selectedInstancePath);
            RefreshHomeDashboard();
            ShaderResultsLabel.Text = $"{shader.Title} instalado em Minecraft {version}.";
        }
        catch (Exception ex)
        {
            ShaderResultsLabel.Text = "Falha na instalação do shader.";
            MessageBox.Show(ex.Message, "Instalar shader", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void InstallResourcePack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthMod pack || ResourcePackGameVersionBox.SelectedItem is not string selectedVersion) return;
        var version = ChooseModInstallVersion(pack.Title, [selectedVersion]);
        if (version is null) return;
        try { if (!await ConfirmDownloadSpaceAsync(version, await _modrinth.EstimateResourcePackInstallAsync(pack.ProjectId, version))) return; }
        catch (Exception ex) { MessageBox.Show($"Não foi possível estimar o download: {ex.Message}\n\nA instalação foi cancelada.", "Estimativa de espaço", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var selectedInstancePath = GetCurrentInstancePath(version);
        if (!CreatePreChangeBackup(version, selectedInstancePath)) return;
        try
        {
            ResourcePackResultsLabel.Text = $"Instalando {pack.Title} em Minecraft {version}…";
            var target = Path.Combine(selectedInstancePath, "resourcepacks");
            await _modrinth.InstallResourcePackAsync(pack, version, target);
            VersionSelector.SelectedItem = version;
            RefreshInstanceList();
            RefreshHomeDashboard();
            ResourcePackResultsLabel.Text = $"{pack.Title} instalado em Minecraft {version}.";
        }
        catch (Exception ex)
        {
            ResourcePackResultsLabel.Text = "Falha na instalação do resource pack.";
            MessageBox.Show(ex.Message, "Instalar resource pack", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyLibraryFilter() => ModResultsList.ItemsSource = _searchResults.ToArray();

    private async void PreviousModPage_Click(object sender, RoutedEventArgs e)
    {
        if (_modSearchPage <= 0) return;
        _modSearchPage--;
        await SearchModsAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void NextModPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_modSearchPage + 1) * ModSearchPageSize >= _modSearchTotal) return;
        _modSearchPage++;
        await SearchModsAsync(showErrors: false, keepCurrentPage: true);
    }

    private async void OpenModDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthMod mod || ModLoaderBox.SelectedItem is not string loader) return;
        if (!_openingManagedModUpdate) _managedModToUpdate = null;
        _activeModDetails = mod;
        _loadedModDetails = null;
        _loadingModDetails = true;
        ModDetailsIcon.Source = string.IsNullOrWhiteSpace(mod.IconUrl) ? null : new System.Windows.Media.Imaging.BitmapImage(new Uri(mod.IconUrl));
        ModDetailsTitle.Text = mod.Title;
        ModDetailsSubtitle.Text = $"{mod.Downloads:N0} downloads · Minecraft Java / {loader}";
        ModDetailsCategories.Text = "Carregando categorias...";
        ModDetailsDescription.Text = mod.Description;
        ModDetailsBody.Text = "Carregando página do projeto…";
        ModDetailsGallery.ItemsSource = null;
        ModDetailsGallery.Visibility = Visibility.Collapsed;
        ModDetailsGalleryFallback.Visibility = Visibility.Visible;
        DetailGameVersionBox.ItemsSource = GameVersionCatalog.SupportedVersions;
        DetailGameVersionBox.SelectedItem = LibraryGameVersionBox.SelectedItem as string ?? GameVersionCatalog.DefaultVersion;
        DetailModVersionBox.ItemsSource = null;
        DetailReleaseInfo.Text = "Buscando as versões mais recentes para esta versão do Minecraft…";
        LibraryTabs.Visibility = Visibility.Collapsed;
        ModDetailsView.Visibility = Visibility.Visible;
        _ = RefreshDiscordPresenceAsync();
        try
        {
            _loadedModDetails = await _modrinth.GetProjectDetailsAsync(mod.ProjectId);
            ModDetailsTitle.Text = _loadedModDetails.Title;
            ModDetailsSubtitle.Text = $"{_loadedModDetails.Downloads:N0} downloads · {_loadedModDetails.Followers:N0} seguidores · Minecraft Java / {loader}";
            ModDetailsCategories.Text = _loadedModDetails.Categories.Count == 0
                ? "Sem categorias informadas"
                : string.Join("  ·  ", _loadedModDetails.Categories.Select(FormatModCategory));
            ModDetailsDescription.Text = _loadedModDetails.Description;
            ModDetailsBody.Text = FormatModDescription(_loadedModDetails.Body);
            ModDetailsGallery.ItemsSource = _loadedModDetails.Gallery;
            ModDetailsGallery.Visibility = _loadedModDetails.Gallery.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModDetailsGalleryFallback.Visibility = _loadedModDetails.Gallery.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception)
        {
            ModDetailsCategories.Text = "Não foi possível carregar todas as informações do projeto.";
            ModDetailsBody.Text = mod.Description;
            LibraryStatusText.Text = "A página detalhada está incompleta; a busca continua disponível.";
        }
        finally { _loadingModDetails = false; }
        DetailInstallButton.Content = _managedModToUpdate is null ? "Instalar versão selecionada" : "Atualizar para esta versão";
        await LoadDetailReleasesAsync();
    }

    private void BackFromModDetails_Click(object sender, RoutedEventArgs e)
    {
        _managedModToUpdate = null;
        DetailInstallButton.Content = "Instalar versão selecionada";
        ModDetailsView.Visibility = Visibility.Collapsed;
        LibraryTabs.Visibility = Visibility.Visible;
        _ = RefreshDiscordPresenceAsync();
    }

    private async void DetailGameVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingModDetails && IsLoaded && _activeModDetails is not null)
            await LoadDetailReleasesAsync();
    }

    private void DetailModVersion_Changed(object sender, SelectionChangedEventArgs e) => UpdateDetailReleaseInfo();

    private async Task LoadDetailReleasesAsync()
    {
        if (_activeModDetails is null || DetailGameVersionBox.SelectedItem is not string gameVersion || ModLoaderBox.SelectedItem is not string loader) return;
        DetailModVersionBox.IsEnabled = false;
        DetailInstallButton.IsEnabled = false;
        DetailReleaseInfo.Text = $"Buscando versões para Minecraft {gameVersion}…";
        try
        {
            var releases = await _modrinth.GetSupportedReleasesAsync(_activeModDetails.ProjectId, loader, new[] { gameVersion });
            DetailModVersionBox.ItemsSource = releases.Select(release => new ComboBoxItem { Content = release.Label, Tag = release }).ToArray();
            DetailModVersionBox.SelectedIndex = releases.Count > 0 ? 0 : -1;
            DetailModVersionBox.IsEnabled = releases.Count > 0;
            DetailInstallButton.IsEnabled = releases.Count > 0;
            if (releases.Count == 0) DetailReleaseInfo.Text = $"Nenhuma versão publicada para Minecraft {gameVersion} / {loader}.";
            else UpdateDetailReleaseInfo();
        }
        catch (Exception ex)
        {
            DetailReleaseInfo.Text = "Falha ao carregar versões do Modrinth.";
            LibraryStatusText.Text = ex.Message;
        }
    }

    private void UpdateDetailReleaseInfo()
    {
        if (DetailModVersionBox.SelectedItem is not ComboBoxItem { Tag: ModrinthReleaseOption release }) return;
        var published = DateTimeOffset.TryParse(release.DatePublished, out var date) ? date.ToLocalTime().ToString("dd/MM/yyyy") : "data indisponível";
        DetailReleaseInfo.Text = $"Publicada em {published} · Minecraft {string.Join(", ", release.GameVersions)} · {release.VersionType}";
        DetailChangelog.Text = string.IsNullOrWhiteSpace(release.Changelog) ? "Esta publicação não tem changelog." : FormatModDescription(release.Changelog);
    }

    private async void InstallSelectedDetailMod_Click(object sender, RoutedEventArgs e)
    {
        if (_activeModDetails is null || DetailGameVersionBox.SelectedItem is not string gameVersion ||
            DetailModVersionBox.SelectedItem is not ComboBoxItem { Tag: ModrinthReleaseOption release } || ModLoaderBox.SelectedItem is not string loader) return;
        var compatibleVersions = release.GameVersions.Where(GameVersionCatalog.IsSupported).ToArray();
        var targetVersion = ChooseModInstallVersion(_activeModDetails.Title, compatibleVersions);
        if (targetVersion is null) return;
        try { if (!await ConfirmDownloadSpaceAsync(targetVersion, await _modrinth.EstimateModInstallAsync(_activeModDetails.ProjectId, targetVersion, loader, release.VersionId))) return; }
        catch (Exception ex) { MessageBox.Show($"Não foi possível estimar o download: {ex.Message}\n\nA instalação foi cancelada.", "Estimativa de espaço", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var selectedInstancePath = GetCurrentInstancePath(targetVersion);
        if (!CreatePreChangeBackup(targetVersion, selectedInstancePath)) return;
        try
        {
            DetailInstallButton.IsEnabled = false;
            LibraryStatusText.Text = $"Instalando {_activeModDetails.Title} {release.VersionNumber} em Minecraft {targetVersion}…";
            var modsDirectory = Path.Combine(selectedInstancePath, "mods");
            if (_managedModToUpdate is { } installed && string.Equals(installed.ProjectId, _activeModDetails.ProjectId, StringComparison.OrdinalIgnoreCase))
                await _modrinth.UpdateManagedModAsync(installed, targetVersion, loader, modsDirectory, release.VersionId);
            else
                await _modrinth.InstallModAsync(_activeModDetails, targetVersion, loader, modsDirectory, release.VersionId);
            VersionSelector.SelectedItem = targetVersion;
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"{_activeModDetails.Title} {release.VersionNumber} instalado/atualizado em Minecraft {targetVersion}.";
            _managedModToUpdate = null;
            DetailInstallButton.Content = "Instalar versão selecionada";
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = "Falha na instalação.";
            MessageBox.Show(ex.Message, "Instalar mod", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { DetailInstallButton.IsEnabled = true; }
    }

    private static string FormatModCategory(string category) => category.ToLowerInvariant() switch
    {
        "optimization" => "Desempenho",
        "technology" => "Tecnologia",
        "utility" => "Utilidade",
        "adventure" => "Aventura",
        "decoration" => "Decoração",
        "game-mechanics" => "Mecânicas de jogo",
        "library" => "Biblioteca",
        "magic" => "Magia",
        "management" => "Gerenciamento",
        "minigame" => "Minijogos",
        "mobs" => "Criaturas",
        "social" => "Social",
        "storage" => "Armazenamento",
        "transportation" => "Transporte",
        "worldgen" => "Geração de mundo",
        _ => System.Globalization.CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(category.Replace('-', ' '))
    };

    private static string FormatModDescription(string markdown)
    {
        var text = System.Net.WebUtility.HtmlDecode(markdown ?? "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\((https?://[^)]+)\)", "$1 — $2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "• ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[`*_~]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModrinthMod mod || ModLoaderBox.SelectedItem is not string loader) return;
        var version = ChooseModInstallVersion(mod.Title);
        if (version is null) return;
        try { if (!await ConfirmDownloadSpaceAsync(version, await _modrinth.EstimateModInstallAsync(mod.ProjectId, version, loader))) return; }
        catch (Exception ex) { MessageBox.Show($"Não foi possível estimar o download: {ex.Message}\n\nA instalação foi cancelada.", "Estimativa de espaço", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var selectedInstancePath = GetCurrentInstancePath(version);
        if (!CreatePreChangeBackup(version, selectedInstancePath)) return;
        try
        {
            LibraryStatusText.Text = $"Instalando {mod.Title} em Minecraft {version} e baixando dependências...";
            var modsDirectory = Path.Combine(selectedInstancePath, "mods");
            await _modrinth.InstallModAsync(mod, version, loader, modsDirectory);
            _preferredInstallVersion = null;
            VersionSelector.SelectedItem = version;
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeRecentInstances(selectedInstancePath);
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"{mod.Title} instalado na instância {version}.";
        }
        catch (Exception ex) { LibraryStatusText.Text = "Falha na instalação."; MessageBox.Show(ex.Message, "Instalar mod", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ImportLocalJar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar mods .jar",
            Filter = "Mods Minecraft (*.jar)|*.jar",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var version = ChooseModInstallVersion("mods .jar importados");
        if (version is null) return;
        var confirm = MessageBox.Show(
            $"Os arquivos .jar selecionados serão copiados para a instância Minecraft {version}. O launcher pode verificar se o arquivo é um arquivo Java válido, mas não consegue garantir sua compatibilidade ou baixar dependências automaticamente.\n\nContinuar?",
            "Importar mods locais", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        var selectedInstancePath = GetCurrentInstancePath(version);
        if (!CreatePreChangeBackup(version, selectedInstancePath)) return;

        var modsDirectory = Path.Combine(selectedInstancePath, "mods");
        var imported = new List<string>();
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(modsDirectory);
            foreach (var source in dialog.FileNames)
            {
                try
                {
                    if (!string.Equals(Path.GetExtension(source), ".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add(Path.GetFileName(source));
                        continue;
                    }

                    var blockedReason = ModSafetyPolicy.GetBlockedJarReason(source);
                    if (blockedReason is not null)
                    {
                        skipped.Add(Path.GetFileName(source) + " (bloqueado: " + blockedReason + ")");
                        continue;
                    }

                    using (var archive = ZipFile.OpenRead(source))
                    {
                        if (archive.Entries.Count == 0)
                        {
                            skipped.Add(Path.GetFileName(source));
                            continue;
                        }
                    }

                    var destination = Path.Combine(modsDirectory, Path.GetFileName(source));
                    if (File.Exists(destination))
                    {
                        skipped.Add(Path.GetFileName(source) + " (já existe)");
                        continue;
                    }
                    File.Copy(source, destination);
                    imported.Add(Path.GetFileName(source));
                }
                catch (InvalidDataException)
                {
                    skipped.Add(Path.GetFileName(source) + " (arquivo inválido)");
                }
                catch (IOException ex)
                {
                    skipped.Add(Path.GetFileName(source) + $" ({ex.Message})");
                }
            }

            VersionSelector.SelectedItem = version;
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = imported.Count > 0
                ? $"{imported.Count} arquivo(s) .jar importado(s) em Minecraft {version}; {skipped.Count} ignorado(s)."
                : $"Nenhum arquivo foi importado. {skipped.Count} arquivo(s) foram ignorados.";

            var summary = imported.Count > 0
                ? $"Importados em Minecraft {version}:\n• " + string.Join("\n• ", imported)
                : "Nenhum arquivo .jar foi importado.";
            if (skipped.Count > 0) summary += "\n\nIgnorados:\n• " + string.Join("\n• ", skipped);
            MessageBox.Show(summary, "Importação de mods", MessageBoxButton.OK,
                skipped.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.None);
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = "Falha ao importar os arquivos .jar.";
            MessageBox.Show(ex.Message, "Importar mods", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UpdateSingleManagedMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedMod mod || !mod.IsManaged || VersionSelector.SelectedItem is not string gameVersion) return;
        _managedModToUpdate = mod;
        _openingManagedModUpdate = true;
        ShowPage(ModrinthPage, ModsNavButton);
        LibraryTitle.Text = "Biblioteca";
        LibrarySubtitle.Text = "Escolha uma versão compatível para atualizar este mod.";
        LibraryTabs.Visibility = Visibility.Visible;
        LibraryInstancesView.Visibility = Visibility.Collapsed;
        LibraryTabs.SelectedItem = ExploreModsTab;
        LibraryGameVersionBox.SelectedItem = gameVersion;
        ModLoaderBox.SelectedItem = mod.Loader;
        OpenModDetails_Click(new Button { Tag = new ModrinthMod(mod.ProjectId, mod.Name, "", "", mod.IconUrl, 0, "") }, new RoutedEventArgs());
        _openingManagedModUpdate = false;
    }

    private void RemoveSingleManagedMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedMod mod || VersionSelector.SelectedItem is not string gameVersion) return;
        if (MessageBox.Show($"Remover {mod.Name} da instância Minecraft {gameVersion}?", "Remover mod", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var selectedInstancePath = GetCurrentInstancePath(gameVersion);
        if (!CreatePreChangeBackup(gameVersion, selectedInstancePath)) return;
        try
        {
            var modsDirectory = Path.Combine(selectedInstancePath, "mods");
            ModrinthService.RemoveInstalledMod(mod, modsDirectory);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"{mod.Name} removido da instância {gameVersion}.";
        }
        catch (Exception ex) { LibraryStatusText.Text = "Não foi possível remover o mod."; MessageBox.Show(ex.Message, "Remover mod", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private string? ChooseModInstallVersion(string modName, IReadOnlyCollection<string>? compatibleVersions = null)
    {
        var allowedVersions = GameVersionCatalog.SupportedVersions
            .Where(version => compatibleVersions is null || compatibleVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (allowedVersions.Length == 0)
        {
            MessageBox.Show("Esta versão do mod não é compatível com nenhuma versão liberada pelo launcher.", "Versão incompatível", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var savedInstances = GameInstanceManager.GetInstances().Where(instance => allowedVersions.Contains(instance.Version, StringComparer.OrdinalIgnoreCase)).ToArray();
        var savedByVersion = savedInstances.GroupBy(instance => instance.Version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var choices = new List<ModInstallTarget>();
        foreach (var version in allowedVersions)
        {
            if (savedByVersion.TryGetValue(version, out var instances))
            {
                var current = instances.FirstOrDefault(item => string.Equals(item.Directory, _selectedManagedInstancePath, StringComparison.OrdinalIgnoreCase));
                var destination = current is not null ? $"Usar {current.Name}" : instances.Length > 1 ? $"Usar instância selecionada ou padrão ({instances.Length} disponíveis)" : $"Usar {instances[0].Name}";
                choices.Add(new ModInstallTarget($"{destination} · Minecraft {version}", version));
            }
            else choices.Add(new ModInstallTarget($"Criar instância · Minecraft {version}", version));
        }

        var dialog = new Window
        {
            Title = "Escolher destino do mod",
            Width = 520,
            MinHeight = 250,
            MaxHeight = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ShowInTaskbar = false,
            Owner = this,
            Background = (Brush)FindResource("WindowBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };
        var content = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22),
            Margin = new Thickness(10)
        };
        var layout = new StackPanel();
        layout.Children.Add(new TextBlock { Text = $"Instalar {modName}", FontSize = 19, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(new TextBlock
        {
            Text = savedInstances.Length > 0
                ? "Escolha uma instância existente ou crie uma para outra versão. A compatibilidade será verificada no download."
                : "Ainda não há instâncias. Escolha a versão para criar somente a pasta necessária para este mod.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 7, 0, 14)
        });
        var selector = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(ModInstallTarget.Label),
            SelectedValuePath = nameof(ModInstallTarget.Version),
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var preferredVersion = _preferredInstallVersion ?? LibraryGameVersionBox.SelectedItem as string;
        selector.SelectedValue = choices.Any(choice => choice.Version == preferredVersion) ? preferredVersion : choices[0].Version;
        layout.Children.Add(selector);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancelButton = new Button { Content = "Cancelar", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), Background = (Brush)FindResource("SecondaryButtonBrush") };
        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        var installButton = new Button { Content = "Continuar", Padding = new Thickness(16, 8, 16, 8), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        installButton.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancelButton);
        actions.Children.Add(installButton);
        layout.Children.Add(actions);
        content.Child = layout;
        dialog.Content = content;
        return dialog.ShowDialog() == true ? selector.SelectedValue as string : null;
    }

    private async void UpdateMods_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ModLoaderBox.SelectedItem is not string loader) return;
        try
        {
            var modsDirectory = Path.Combine(GetCurrentInstancePath(version), "mods");
            LibraryStatusText.Text = $"Verificando versões compatíveis com Minecraft {version} e {loader}...";
            var updates = await _modrinth.CheckManagedModUpdatesAsync(version, loader, modsDirectory);
            if (updates.Count == 0)
            {
                LibraryStatusText.Text = "Tudo atualizado: os mods gerenciados já estão nas versões compatíveis mais recentes.";
                return;
            }

            var preview = string.Join(Environment.NewLine, updates.Take(12).Select(update =>
                $"• {update.Name}: {update.InstalledVersion} → {update.LatestCompatibleVersion}"));
            if (updates.Count > 12) preview += $"{Environment.NewLine}… e mais {updates.Count - 12} atualização(ões).";
            var confirm = MessageBox.Show(
                $"Foram encontradas {updates.Count} atualização(ões) compatíveis com Minecraft {version} / {loader}:\n\n{preview}\n\nDeseja baixar e instalar agora?",
                "Atualizações de mods",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes)
            {
                LibraryStatusText.Text = $"{updates.Count} atualização(ões) disponível(is); nada foi alterado.";
                return;
            }

            var selectedInstancePath = GetCurrentInstancePath(version);
            if (!CreatePreChangeBackup(version, selectedInstancePath)) return;

            LibraryStatusText.Text = "Baixando atualizações selecionadas...";
            var count = await _modrinth.UpdateManagedModsAsync(version, loader, modsDirectory);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = count == 0 ? "Os mods já estavam atualizados." : $"{count} mod(s) atualizado(s) para Minecraft {version} / {loader}.";
        }
        catch (Exception ex) { LibraryStatusText.Text = "Não foi possível verificar atualizações."; MessageBox.Show(ex.Message, "Atualização de mods", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Importar modpack do Modrinth", Filter = "Modrinth modpack (*.mrpack)|*.mrpack", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var importer = new ModrinthPackImporter();
            var manifest = await importer.ReadManifestAsync(dialog.FileName);
            if (!GameVersionCatalog.IsSupported(manifest.GameVersion))
            {
                MessageBox.Show($"Este modpack usa Minecraft {manifest.GameVersion}, que não está liberado no SAPVP-AI. Versões permitidas: {string.Join(", ", GameVersionCatalog.SupportedVersions)}.", "Versão não permitida", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var version = ChooseModInstallVersion(manifest.Name, [manifest.GameVersion]);
            if (version is null) return;
            var selectedInstancePath = GetCurrentInstancePath(version);
            var confirm = MessageBox.Show(
                $"Pacote: {manifest.Name}\nMinecraft: {manifest.GameVersion}\nFabric Loader: {manifest.FabricLoaderVersion}\nArquivos listados: {manifest.FileCount}\n\nOs arquivos serão baixados e validados antes de concluir. Deseja continuar?",
                "Resumo do modpack", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes || !CreatePreChangeBackup(version, selectedInstancePath)) return;

            var progressWindow = new Window
            {
                Title = "Importando modpack", Owner = this, Width = 460, Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
            };
            var progressLabel = new TextBlock { Text = "Preparando arquivos...", Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap };
            var progressBar = new ProgressBar { Height = 18, Minimum = 0, Maximum = Math.Max(1, manifest.FileCount), Margin = new Thickness(0, 0, 0, 14) };
            var cancelSource = new System.Threading.CancellationTokenSource();
            var cancelButton = new Button { Content = "Cancelar", Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right };
            cancelButton.Click += (_, _) => { cancelSource.Cancel(); progressLabel.Text = "Cancelando com segurança..."; cancelButton.IsEnabled = false; };
            progressWindow.Content = new StackPanel { Margin = new Thickness(20), Children = { progressLabel, progressBar, cancelButton } };
            progressWindow.Show();
            LibraryStatusText.Text = "Importando modpack...";
            ModrinthPackImportResult result;
            try
            {
                var progress = new Progress<ModrinthPackImportProgress>(update =>
                {
                    progressBar.Value = update.Completed;
                    progressLabel.Text = $"{update.Completed} de {update.Total}: {Path.GetFileName(update.CurrentFile)}";
                });
                result = await importer.ImportAsync(dialog.FileName, selectedInstancePath, version, progress, cancelSource.Token);
            }
            finally { progressWindow.Close(); cancelSource.Dispose(); }
            VersionSelector.SelectedItem = version;
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"{result.Name}: {result.FilesInstalled} arquivos baixados, {result.ConfigFilesExtracted} configurações e {(result.OptionsFileExtracted ? "options.txt" : "sem options.txt no pacote")}.";
            MessageBox.Show($"Importação concluída.\n\nPacote: {result.Name}\nMinecraft: {result.GameVersion} · Fabric {result.LoaderVersion}\nMods e arquivos baixados: {result.FilesInstalled}\nArquivos ignorados (somente servidor): {result.FilesSkipped}\nArquivos em config/defaultconfigs: {result.ConfigFilesExtracted}\nOpções e teclas (options.txt): {(result.OptionsFileExtracted ? "incluídas" : "não vieram neste pacote")}\nOverrides extraídos no total: {result.OverridesExtracted}\nDestino: {selectedInstancePath}\n\nUm backup anterior à importação foi criado.", "Relatório de importação", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { LibraryStatusText.Text = "Importação cancelada. Arquivos já concluídos podem permanecer na instância; use o backup para restaurar o estado anterior."; MessageBox.Show(LibraryStatusText.Text, "Importação cancelada", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { LibraryStatusText.Text = "Falha na importação do pacote."; MessageBox.Show(ex.Message, "Importar modpack", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RepairInstance_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version)) return;
        var modsDirectory = Path.Combine(GetCurrentInstancePath(version), "mods");
        if (!Directory.Exists(modsDirectory))
        {
            MessageBox.Show("Esta instância ainda não possui uma pasta de mods para verificar.", "Verificar instância", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var broken = new List<string>();
        foreach (var jar in Directory.EnumerateFiles(modsDirectory, "*.jar", System.IO.SearchOption.TopDirectoryOnly))
        {
            try { using var archive = ZipFile.OpenRead(jar); if (archive.Entries.Count == 0) broken.Add(Path.GetFileName(jar)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { broken.Add(Path.GetFileName(jar)); }
        }
        var managed = ModrinthService.LoadInstalledMods(modsDirectory);
        var missingManaged = managed.Where(mod => !File.Exists(Path.Combine(modsDirectory, mod.FileName))).Select(mod => mod.Name).ToArray();
        var blocked = ModSafetyPolicy.FindBlockedJars(modsDirectory);
        var result = new List<string>();
        result.Add(broken.Count == 0 ? "Arquivos JAR: estrutura íntegra." : $"JARs inválidos: {string.Join(", ", broken)}.");
        result.Add(missingManaged.Length == 0 ? "Mods gerenciados: arquivos presentes." : $"Arquivos ausentes: {string.Join(", ", missingManaged)}.");
        result.Add(blocked.Count == 0 ? "Política de mods: sem bloqueios detectados." : $"Mods bloqueados: {string.Join(", ", blocked)}.");
        if (managed.Count > 0)
        {
            var answer = MessageBox.Show(string.Join(Environment.NewLine, result) + "\n\nDeseja atualizar os mods instalados pelo Modrinth para versões compatíveis? Será criado um backup antes.",
                "Verificação e reparo", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes && CreatePreChangeBackup(version, GetCurrentInstancePath(version)))
            {
                try
                {
                    var updated = await _modrinth.UpdateManagedModsAsync(version, "fabric", modsDirectory);
                    result.Add($"Reparo concluído: {updated} mod(s) gerenciado(s) atualizado(s).");
                }
                catch (Exception ex) { result.Add($"A atualização não foi concluída: {ex.Message}"); }
            }
        }
        else result.Add("Nenhum mod gerenciado pelo Modrinth pode ser reinstalado automaticamente.");
        MessageBox.Show(string.Join(Environment.NewLine, result), "Relatório da instância", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RepairGameFiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstanceInfo instance }) return;
        await RepairGameFilesAsync(instance);
    }

    private async Task RepairGameFilesAsync(GameInstanceInfo instance)
    {
        var answer = MessageBox.Show(
            $"Verificar e reparar os arquivos oficiais do Minecraft {instance.Version} nesta instância?\n\nO launcher vai baixar novamente o runtime oficial se necessário. Mods, saves e configurações pessoais serão preservados.",
            "Reparar arquivos do jogo", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            LibraryStatusText.Text = $"Verificando arquivos oficiais de Minecraft {instance.Version}…";
            var minecraftLauncher = new MinecraftLauncher(new MinecraftPath(instance.Directory));
            await minecraftLauncher.InstallAsync(instance.Version);

            var modsPath = Path.Combine(instance.Directory, "mods");
            var isFabric = Directory.Exists(modsPath) && Directory.EnumerateFiles(modsPath, "*.jar").Any();
            var marker = Path.Combine(instance.Directory, "sapvp-instance.json");
            if (File.Exists(marker))
            {
                try
                {
                    using var metadata = JsonDocument.Parse(File.ReadAllText(marker));
                    isFabric |= metadata.RootElement.TryGetProperty("loader", out var loader) && string.Equals(loader.GetString(), "fabric", StringComparison.OrdinalIgnoreCase);
                }
                catch (JsonException) { }
            }
            if (isFabric)
            {
                LibraryStatusText.Text = "Arquivos base verificados; conferindo o Fabric…";
                var fabric = new FabricInstaller(new HttpClient());
                var loaderVersion = GetImportedFabricLoaderVersion(instance.Directory);
                if (string.IsNullOrWhiteSpace(loaderVersion)) await fabric.Install(instance.Version, new MinecraftPath(instance.Directory));
                else await fabric.Install(instance.Version, loaderVersion, new MinecraftPath(instance.Directory));
            }

            LibraryStatusText.Text = $"Arquivos do Minecraft {instance.Version} reparados.";
            RefreshInstanceList();
            MessageBox.Show("Os arquivos do jogo foram verificados. Mods, saves e configurações foram mantidos.", "Reparo concluído", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = "Não foi possível concluir o reparo dos arquivos do jogo.";
            MessageBox.Show(ex.Message, "Reparo do Minecraft", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportInstancePackage_Click(GameInstanceInfo instance)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar instância SAPVP-AI",
            FileName = $"{instance.Name}.sapvp-instance.zip",
            Filter = "Pacote SAPVP-AI (*.sapvp-instance.zip)|*.sapvp-instance.zip",
            AddExtension = true,
            DefaultExt = ".sapvp-instance.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            GameInstanceArchiveService.Export(instance, dialog.FileName);
            LibraryStatusText.Text = $"Pacote exportado: {Path.GetFileName(dialog.FileName)}";
            MessageBox.Show("Pacote exportado. Ele inclui mods, saves e configurações selecionadas; os arquivos oficiais do jogo serão baixados novamente ao iniciar no outro computador.", "Exportação concluída", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Exportar instância", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ImportInstancePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Importar pacote SAPVP-AI", Filter = "Pacote SAPVP-AI (*.sapvp-instance.zip;*.zip)|*.sapvp-instance.zip;*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var package = GameInstanceArchiveService.ReadPackageInfo(dialog.FileName);
            var name = AskForProfileName("Importar instância", package.Name, "Nome da instância");
            if (string.IsNullOrWhiteSpace(name)) return;
            var instance = GameInstanceArchiveService.Import(dialog.FileName, name);
            RefreshInstanceList();
            RefreshHomeRecentInstances(instance.Directory);
            RefreshHomeDashboard();
            OpenInstanceProfiles(instance);
            LibraryStatusText.Text = $"Instância '{instance.Name}' importada. Minecraft {instance.Version} será instalado ao iniciar.";
            MessageBox.Show($"Instância importada: {instance.Name}\nMinecraft {instance.Version}\n\nMods, saves e configurações foram extraídos. O jogo oficial será baixado ao iniciar.", "Importação concluída", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Importar instância", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DuplicateInstance_Click(GameInstanceInfo instance)
    {
        var name = AskForProfileName("Duplicar instância", $"{instance.Name} - Cópia", "Nome da instância");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var duplicate = GameInstanceArchiveService.Duplicate(instance, name);
            RefreshInstanceList();
            RefreshHomeRecentInstances(duplicate.Directory);
            RefreshHomeDashboard();
            OpenInstanceProfiles(duplicate);
            LibraryStatusText.Text = $"Instância '{duplicate.Name}' criada como cópia independente.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Duplicar instância", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RenameInstance_Click(GameInstanceInfo instance)
    {
        var name = AskForProfileName("Renomear instância", instance.Name, "Nome da instância");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var renamed = GameInstanceManager.RenameInstance(instance, name);
            RefreshInstanceList();
            RefreshHomeRecentInstances(renamed.Directory);
            RefreshHomeDashboard();
            if (string.Equals(_selectedManagedInstancePath, renamed.Directory, StringComparison.OrdinalIgnoreCase))
            {
                LibrarySubtitle.Text = $"Gerencie {renamed.Name} · Minecraft {renamed.Version} e seus arquivos separados.";
                RefreshModrinthInstance();
            }
            LibraryStatusText.Text = $"Instância renomeada para '{renamed.Name}'.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Renomear instância", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is string version) OpenFolder(Path.Combine(GetCurrentInstancePath(version), "mods"));
    }

    private void RefreshModrinthInstance()
    {
        if (ManagedModsList is null) return;
        if (VersionSelector?.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version))
        {
            SelectedInstancePath.Text = "Selecione uma instância da lista para gerenciar.";
            InstancePromptText.Text = "Escolha uma instância existente ou comece selecionando uma versão na página Início.";
            ManagedModsList.ItemsSource = null;
            ManagedResourcePacksList.ItemsSource = null;
            ManagedWorldsList.ItemsSource = null;
            ManagedShadersList.ItemsSource = null;
            InstanceAssetTabs.Visibility = Visibility.Collapsed;
            InstanceSelectionPrompt.Visibility = Visibility.Visible;
            InstanceToolsPanel.Visibility = Visibility.Collapsed;
            InstanceAddModsButton.IsEnabled = false;
            return;
        }
        var instancePath = GetCurrentInstancePath(version);
        var modsDirectory = Path.Combine(instancePath, "mods");
        var instanceExists = Directory.Exists(instancePath);
        InstanceAddModsButton.IsEnabled = true;
        var selectedInfo = GameInstanceManager.GetInstances().FirstOrDefault(item => string.Equals(item.Directory, instancePath, StringComparison.OrdinalIgnoreCase));
        var isCustom = IsCustomInstance(instancePath);
        SelectedInstancePath.Text = Directory.Exists(instancePath)
            ? $"{selectedInfo?.Name ?? $"Minecraft {version}"} · Minecraft {version} · arquivos separados"
            : $"Minecraft {version} · ainda não criada";
        SelectedInstancePath.ToolTip = instancePath;
        InstanceSelectionPrompt.Visibility = instanceExists ? Visibility.Collapsed : Visibility.Visible;
        InstanceAssetTabs.Visibility = instanceExists ? Visibility.Visible : Visibility.Collapsed;
        InstanceToolsPanel.Visibility = instanceExists ? Visibility.Visible : Visibility.Collapsed;
        if (!instanceExists)
        {
            InstancePromptText.Text = $"Minecraft {version} ainda não tem uma instância. Instale um mod pela Biblioteca ou inicie o jogo na página Início.";
            ManagedModsList.ItemsSource = null;
            ManagedResourcePacksList.ItemsSource = null;
            ManagedWorldsList.ItemsSource = null;
            ManagedShadersList.ItemsSource = null;
            ProfileSelector.ItemsSource = null;
            return;
        }
        ManagedModsList.Visibility = Visibility.Visible;
        ProfileSelector.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        ActivateProfileButton.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        if (InstanceActionsButton.ContextMenu is ContextMenu instanceMenu)
            foreach (var menuItem in instanceMenu.Items.OfType<MenuItem>().Where(menuItem => menuItem.Header?.ToString()?.Contains("perfil", StringComparison.OrdinalIgnoreCase) == true)) menuItem.Visibility = Visibility.Visible;
        if (!isCustom) RefreshProfileSelector(version);
        else
        {
            ProfileSelector.ItemsSource = null;
            if (InstanceActionsButton.ContextMenu is ContextMenu menu)
                foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Header?.ToString()?.Contains("perfil", StringComparison.OrdinalIgnoreCase) == true)) item.Visibility = Visibility.Collapsed;
        }
        var managed = ModrinthService.LoadInstalledMods(modsDirectory).ToList();
        var trackedFiles = managed.Select(mod => mod.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(modsDirectory)) foreach (var modFile in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            var fileName = Path.GetFileName(modFile);
            if (!trackedFiles.Contains(fileName)) managed.Add(new ManagedMod("", fileName, "", "Modpack", fileName, version, "Fabric"));
        }
        ManagedModsList.ItemsSource = managed;
        ManagedResourcePacksList.ItemsSource = GetManagedFiles(Path.Combine(instancePath, "resourcepacks"), ".zip", "Textura");
        ManagedWorldsList.ItemsSource = GetManagedWorlds(Path.Combine(instancePath, "saves"));
        ManagedShadersList.ItemsSource = GetManagedFiles(Path.Combine(instancePath, "shaderpacks"), ".zip", "Shader");
        UpdateInstanceAssetAction();
        _ = LoadMissingManagedModIconsAsync(managed, version, instancePath);
    }

    private static ManagedInstanceAsset[] GetManagedFiles(string folder, string extension, string type)
    {
        if (!Directory.Exists(folder)) return [];
        var items = Directory.EnumerateFiles(folder, "*", System.IO.SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            .Select(path => new ManagedInstanceAsset(Path.GetFileName(path), type + " instalado", path, false)).ToList();
        foreach (var path in Directory.EnumerateDirectories(folder, "*", System.IO.SearchOption.TopDirectoryOnly))
        {
            var validPackDirectory = type == "Textura"
                ? File.Exists(Path.Combine(path, "pack.mcmeta"))
                : Directory.Exists(Path.Combine(path, "shaders")) || File.Exists(Path.Combine(path, "shaders.properties"));
            if (validPackDirectory) items.Add(new ManagedInstanceAsset(Path.GetFileName(path), type + " instalado · pasta", path, true));
        }
        return items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static ManagedInstanceAsset[] GetManagedWorlds(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder, "*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(path => File.Exists(Path.Combine(path, "level.dat")))
                .Select(path => new ManagedInstanceAsset(Path.GetFileName(path), "Mundo salvo", path, true))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : [];

    private void InstanceAssetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInstanceAssetAction();

    private void UpdateInstanceAssetAction()
    {
        if (InstanceAddModsButton is null) return;
        var tab = InstanceAssetTabs?.SelectedItem as TabItem;
        InstanceAddModsButton.Content = tab?.Header?.ToString() switch
        {
            "Texturas" => "Adicionar textura",
            "Mundos" => "Abrir pasta de mundos",
            "Shaders instalados" => "Adicionar shader",
            _ => "Adicionar mods"
        };
    }

    private void AddCurrentInstanceAsset_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || !GameVersionCatalog.IsSupported(version)) return;
        switch ((InstanceAssetTabs.SelectedItem as TabItem)?.Header?.ToString())
        {
            case "Texturas": ImportInstanceZipAsset(version, "resourcepacks", "resource pack"); break;
            case "Mundos": OpenFolder(Path.Combine(GetCurrentInstancePath(version), "saves")); break;
            case "Shaders instalados": ImportInstanceZipAsset(version, "shaderpacks", "shader pack"); break;
            default: BrowseModsForSelectedInstance_Click(sender, e); break;
        }
    }

    private void ImportInstanceZipAsset(string version, string folderName, string displayName)
    {
        var dialog = new OpenFileDialog { Title = $"Adicionar {displayName}", Filter = "Arquivos ZIP (*.zip)|*.zip", Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var instancePath = GetCurrentInstancePath(version);
        if (!CreatePreChangeBackup(version, instancePath)) return;
        try
        {
            using (var archive = ZipFile.OpenRead(dialog.FileName))
                if (archive.Entries.Count == 0) throw new InvalidDataException("O arquivo ZIP está vazio.");
            var target = Path.Combine(instancePath, folderName);
            Directory.CreateDirectory(target);
            var destination = Path.Combine(target, Path.GetFileName(dialog.FileName));
            if (File.Exists(destination)) throw new IOException("Já existe um arquivo com esse nome nesta instância.");
            File.Copy(dialog.FileName, destination);
            RefreshModrinthInstance();
            RefreshInstanceList();
            LibraryStatusText.Text = $"{displayName} adicionado à instância {version}.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, $"Adicionar {displayName}", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OpenInstanceAsset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedInstanceAsset asset || !File.Exists(asset.FullPath) && !Directory.Exists(asset.FullPath)) return;
        OpenFolder(asset.IsDirectory ? asset.FullPath : Path.GetDirectoryName(asset.FullPath)!);
    }

    private void RemoveInstanceAsset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ManagedInstanceAsset asset || VersionSelector.SelectedItem is not string version) return;
        if (MessageBox.Show($"Enviar {asset.Name} para a Lixeira?", "Remover item", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var root = Path.GetFullPath(GetCurrentInstancePath(version)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(asset.FullPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("O item não pertence a esta instância.", "Remover item", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!CreatePreChangeBackup(version, GetCurrentInstancePath(version))) return;
        try
        {
            if (asset.IsDirectory) FileSystem.DeleteDirectory(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            RefreshModrinthInstance();
            RefreshInstanceList();
            LibraryStatusText.Text = $"{asset.Name} removido da instância.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Remover item", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task LoadMissingManagedModIconsAsync(List<ManagedMod> managed, string version, string instancePath)
    {
        var modsDirectory = Path.Combine(instancePath, "mods");
        var missing = managed.Where(mod => string.IsNullOrWhiteSpace(mod.IconUrl)).ToArray();
        if (missing.Length == 0) return;
        using var gate = new SemaphoreSlim(3, 3);
        await Task.WhenAll(missing.Select(async mod =>
        {
            var jarPath = Path.GetFullPath(Path.Combine(modsDirectory, mod.FileName));
            var cacheKey = mod.IsManaged ? $"project:{mod.ProjectId}" : $"file:{jarPath}";
            if (!_modIconCache.TryGetValue(cacheKey, out var icon))
            {
                await gate.WaitAsync();
                try
                {
                    if (mod.IsManaged) icon = await _modrinth.GetProjectIconUrlAsync(mod.ProjectId);
                    if (string.IsNullOrWhiteSpace(icon) && File.Exists(jarPath)) icon = await _modrinth.GetProjectIconFromJarAsync(jarPath);
                    _modIconCache[cacheKey] = icon;
                }
                catch { _modIconCache[cacheKey] = null; }
                finally { gate.Release(); }
            }
            if (!string.IsNullOrWhiteSpace(icon))
            {
                var index = managed.FindIndex(item => string.Equals(item.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) managed[index] = managed[index] with { IconUrl = icon };
            }
        }));
        if (VersionSelector.SelectedItem as string == version && ManagedModsList is not null &&
            string.Equals(GetCurrentInstancePath(version), instancePath, StringComparison.OrdinalIgnoreCase))
            ManagedModsList.ItemsSource = managed.ToArray();
    }

    private static string? GetImportedFabricLoaderVersion(string instancePath)
    {
        var metadataPath = Path.Combine(instancePath, "modrinth-pack.json");
        if (!File.Exists(metadataPath)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("loaderVersion", out var loader) ? loader.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static bool IsFabricInstance(string instancePath)
    {
        var metadataPath = Path.Combine(instancePath, "sapvp-instance.json");
        if (!File.Exists(metadataPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("loader", out var loader) && string.Equals(loader.GetString(), "fabric", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private void RefreshInstances_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstanceList();
        RefreshModrinthInstance();
    }

    private void CreateNamedInstance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Criar instância", Owner = this, Width = 440, SizeToContent = SizeToContent.Height,
            MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("WindowBrush"), Foreground = (Brush)FindResource("TextBrush")
        };
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = "Nova instância", FontSize = 20, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(new TextBlock { Text = "Ela terá mods, configurações e saves separados, mesmo usando a mesma versão do Minecraft.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 6, 0, 14) });
        var nameBox = new TextBox { Text = "Meu perfil", Margin = new Thickness(0, 0, 0, 10) };
        var versionBox = new ComboBox { ItemsSource = GameVersionCatalog.SupportedVersions, SelectedItem = GameVersionCatalog.DefaultVersion, Height = 40, VerticalContentAlignment = VerticalAlignment.Center };
        var templateBox = new ComboBox { Height = 40, Margin = new Thickness(0, 10, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        templateBox.Items.Add(new ComboBoxItem { Content = "Vanilla — sem mods", Tag = "vanilla" });
        templateBox.Items.Add(new ComboBoxItem { Content = "Fabric — pronta para mods", Tag = "fabric" });
        templateBox.SelectedIndex = 0;
        layout.Children.Add(nameBox);
        layout.Children.Add(versionBox);
        layout.Children.Add(templateBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var create = new Button { Content = "Criar", Padding = new Thickness(18, 8, 18, 8), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        create.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(create);
        layout.Children.Add(actions);
        dialog.Content = layout;
        if (dialog.ShowDialog() != true || versionBox.SelectedItem is not string version) return;
        try
        {
            var loader = (templateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "fabric" ? "fabric" : null;
            var instance = GameInstanceManager.CreateNamedInstance(version, nameBox.Text, loader);
            RefreshInstanceList();
            RefreshHomeRecentInstances(instance.Directory);
            RefreshHomeDashboard();
            OpenInstanceProfiles(instance);
            LibraryStatusText.Text = $"Instância '{instance.Name}' criada para Minecraft {version}.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Criar instância", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RefreshProfileSelector(string version)
    {
        if (ProfileSelector is null) return;
        _loadingProfiles = true;
        var activeId = ModpackProfileStore.GetActiveProfile(version).Id;
        ProfileSelector.ItemsSource = ModpackProfileStore.GetProfiles(version);
        ProfileSelector.SelectedValue = activeId;
        var active = ModpackProfileStore.GetActiveProfile(version);
        var modCount = ModpackProfileStore.GetModCount(version, active.Id);
        LibraryStatusText.Text = $"Perfil ativo: {active.Name} · {modCount} mod(s)";
        _loadingProfiles = false;
    }

    private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        var profile = ModpackProfileStore.GetProfiles(version).FirstOrDefault(item => item.Id == profileId);
        if (profile is not null) LibraryStatusText.Text = $"Perfil selecionado: {profile.Name} · pressione Ativar para trocar o conjunto de mods.";
    }

    private void CreateModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version) return;
        try
        {
            var profile = ModpackProfileStore.CreateFromCurrent(version, ProfileNameBox.Text);
            ProfileNameBox.Clear();
            RefreshProfileSelector(version);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"Perfil '{profile.Name}' criado com uma cópia dos mods atuais.";
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = ex.Message;
        }
    }

    private void ActivateModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        try
        {
            if (!CreatePreChangeBackup(version)) return;
            ModpackProfileStore.Activate(version, profileId);
            RefreshProfileSelector(version);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"Perfil '{ModpackProfileStore.GetActiveProfile(version).Name}' ativado para Minecraft {version}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Perfis de mods", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenameModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        try
        {
            var renamed = ModpackProfileStore.Rename(version, profileId, ProfileNameBox.Text);
            ProfileNameBox.Clear();
            RefreshProfileSelector(version);
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"Perfil renomeado para '{renamed.Name}'.";
        }
        catch (Exception ex) { LibraryStatusText.Text = ex.Message; }
    }

    private void DeleteModpackProfile_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version || ProfileSelector.SelectedValue is not string profileId) return;
        var profile = ModpackProfileStore.GetProfiles(version).FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return;
        if (profile.Id == "default")
        {
            LibraryStatusText.Text = "O perfil Padrão não pode ser excluído.";
            return;
        }
        var active = ModpackProfileStore.GetActiveProfile(version).Id == profileId;
        var answer = MessageBox.Show(
            $"Excluir o perfil '{profile.Name}'?" + (active ? " O conjunto ativo será salvo e o perfil Padrão será ativado." : ""),
            "Excluir perfil", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            ModpackProfileStore.Delete(version, profileId);
            RefreshProfileSelector(version);
            RefreshModrinthInstance();
            RefreshInstanceList();
            RefreshHomeDashboard();
            LibraryStatusText.Text = $"Perfil '{profile.Name}' excluído.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Perfis de mods", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RefreshInstanceList()
    {
        InstanceList.Children.Clear();
        var instances = GameInstanceManager.GetInstances();
        RefreshHomeRecentInstances();

        if (instances.Count == 0)
        {
            InstanceList.Children.Add(new TextBlock
            {
                Text = "Nenhuma instância criada. Instale um mod escolhendo uma versão ou inicie o jogo pela página Início.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 15,
                Margin = new Thickness(4, 8, 4, 8)
            });
            InstancesFooter.Text = "Nenhuma instância com dados ainda.";
            InstancesFooter.ToolTip = GameInstanceManager.InstancesRoot;
            RefreshModrinthInstance();
            RefreshHomeDashboard();
            return;
        }

        foreach (var instance in instances)
        {
            InstanceList.Children.Add(CreateInstanceCard(instance));
        }

        InstancesFooter.Text = $"{instances.Count} instância(s) salva(s)";
        InstancesFooter.ToolTip = GameInstanceManager.InstancesRoot;
        RefreshModrinthInstance();
        RefreshHomeDashboard();
    }

    private Border CreateInstanceCard(GameInstanceInfo instance)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 14)
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Child = layout;

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(instance.Name) || instance.Name == $"Minecraft {instance.Version}"
                ? $"Minecraft Java {instance.Version}" : $"{instance.Name} · Minecraft {instance.Version}",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        details.Children.Add(new TextBlock
        {
            Text = instance.IsInstalled ? "Jogo instalado" : "Instância criada; jogo será instalado ao iniciar",
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 5, 0, 0)
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{instance.ModCount} mod(s)",
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 5, 0, 0)
        });
        if (IsCustomInstance(instance.Directory))
            details.Children.Add(new TextBlock { Text = "Instância independente", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 5, 0, 0) });
        else
        {
            var activeProfile = ModpackProfileStore.GetActiveProfile(instance.Version);
            details.Children.Add(new TextBlock { Text = $"{ModpackProfileStore.GetProfiles(instance.Version).Count} perfil(is) de mods • ativo: {activeProfile.Name}", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 5, 0, 0) });
        }
        Grid.SetColumn(details, 0);
        layout.Children.Add(details);

        var buttons = new WrapPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 270
        };
        var launchButton = CreateActionButton("Jogar", LaunchInstance_Click);
        launchButton.Tag = instance;
        launchButton.Background = (Brush)FindResource("AccentBrush");
        launchButton.Foreground = Brushes.White;
        buttons.Children.Add(launchButton);
        buttons.Children.Add(CreateActionButton("Gerenciar", (_, _) => OpenInstanceProfiles(instance)));
        var canRemove = !string.Equals(_runningInstancePath, instance.Directory, StringComparison.OrdinalIgnoreCase);
        var moreButton = CreateActionButton("⋯", (_, _) => { });
        moreButton.FontSize = 18;
        moreButton.ToolTip = "Outras ações";
        var menuPanel = new StackPanel { MinWidth = 190, Margin = new Thickness(5) };
        System.Windows.Controls.Primitives.Popup? actionsPopup = null;
        void AddPopupAction(string label, Action action, bool enabled = true)
        {
            var actionButton = new Button
            {
                Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(11, 8, 14, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = (Brush)FindResource("PanelBrush"),
                Foreground = (Brush)FindResource("TextBrush"),
                BorderBrush = (Brush)FindResource("PanelBrush"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = enabled
            };
            actionButton.MouseEnter += (_, _) =>
            {
                actionButton.Background = (Brush)FindResource("SelectedBrush");
                actionButton.BorderBrush = (Brush)FindResource("AccentBrush");
                actionButton.FontWeight = FontWeights.SemiBold;
            };
            actionButton.MouseLeave += (_, _) =>
            {
                actionButton.Background = (Brush)FindResource("PanelBrush");
                actionButton.BorderBrush = (Brush)FindResource("PanelBrush");
                actionButton.FontWeight = FontWeights.Normal;
            };
            actionButton.PreviewMouseLeftButtonDown += (_, _) =>
            {
                actionButton.Background = (Brush)FindResource("AccentBrush");
                actionButton.Foreground = Brushes.White;
            };
            actionButton.Click += (_, _) => { if (actionsPopup is not null) actionsPopup.IsOpen = false; action(); };
            menuPanel.Children.Add(actionButton);
        }
        AddPopupAction("Renomear instância", () => RenameInstance_Click(instance), canRemove);
        AddPopupAction("Duplicar instância", () => DuplicateInstance_Click(instance));
        AddPopupAction("Exportar pacote", () => ExportInstancePackage_Click(instance));
        AddPopupAction("Reparar arquivos do jogo", () => _ = RepairGameFilesAsync(instance), canRemove);
        AddPopupAction("Abrir pasta", () => OpenFolder(instance.Directory));
        var separator = new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), Margin = new Thickness(6, 5, 6, 5) };
        menuPanel.Children.Add(separator);
        AddPopupAction("Remover instância", () => RemoveInstance(instance), canRemove);
        actionsPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = moreButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)FindResource("PanelBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Child = menuPanel
            }
        };
        moreButton.Click += (_, _) => actionsPopup.IsOpen = !actionsPopup.IsOpen;
        buttons.Children.Add(moreButton);
        Grid.SetColumn(buttons, 1);
        layout.Children.Add(buttons);

        return card;
    }

    private void OpenInstanceProfiles(GameInstanceInfo instance)
    {
        VersionSelector.SelectedItem = instance.Version;
        _selectedManagedInstancePath = instance.Directory;
        ShowPage(ModrinthPage, InstancesNavButton);
        LibraryTitle.Text = "Instâncias";
        LibrarySubtitle.Text = $"Gerencie {instance.Name} · Minecraft {instance.Version} e seus arquivos separados.";
        LibraryTabs.Visibility = Visibility.Collapsed;
        LibraryInstancesView.Visibility = Visibility.Visible;
        RefreshModrinthInstance();
    }

    private async void BrowseModsForSelectedInstance_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version) return;
        _preferredInstallVersion = version;
        _preferredInstallInstancePath = _selectedManagedInstancePath;
        LibraryGameVersionBox.SelectedItem = version;
        ShowPage(ModrinthPage, ModsNavButton);
        LibraryTitle.Text = "Biblioteca";
        LibrarySubtitle.Text = "Explore mods e resource packs do Modrinth e organize suas versões.";
        LibraryTabs.Visibility = Visibility.Visible;
        LibraryInstancesView.Visibility = Visibility.Collapsed;
        LibraryTabs.SelectedItem = ExploreModsTab;
        await SearchModsAsync(showErrors: false);
        ModSearchBox.Focus();
    }

    private Button CreateActionButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(5, 4, 0, 4),
            Background = (Brush)FindResource("SecondaryButtonBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += onClick;
        return button;
    }

    private void ShowInstanceContents(GameInstanceInfo instance)
    {
        var folder = Path.Combine(instance.Directory, "mods");
        var entries = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.jar").Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToArray() : [];
        var title = "Mods";
        var contents = entries.Length == 0
            ? "Nenhum mod encontrado nesta instância."
            : string.Join(Environment.NewLine, entries.Take(30)) +
              (entries.Length > 30 ? $"{Environment.NewLine}... e mais {entries.Length - 30}." : string.Empty);
        var result = MessageBox.Show(
            $"Minecraft Java {instance.Version} • {entries.Length} item(ns)\n\n{contents}\n\nPasta: {folder}\n\nAbrir esta pasta no Explorador?",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            OpenFolder(folder);
        }
    }

    private void RemoveInstance(GameInstanceInfo instance)
    {
        if (string.Equals(_runningInstancePath, instance.Directory, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Feche esta sessão do Minecraft antes de remover a instância.", "Instância em uso",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            $"Remover a instância Minecraft Java {instance.Version}?\n\n" +
            "A pasta inclui o jogo instalado, mods, saves do Minecraft e configurações desta versão. " +
            "Ela será enviada para a Lixeira do Windows para que possa ser recuperada.",
            "Remover instância",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            GameInstanceManager.MoveInstanceToRecycleBin(instance);
            RefreshInstanceList();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Não foi possível remover a instância.\n\n{exception.Message}",
                "Erro ao remover", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenFolder(string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
    }

    private void SetLaunchBusy(bool isBusy, string status)
    {
        _launchInProgress = isBusy;
        LaunchButton.IsEnabled = !isBusy;
        VersionSelector.IsEnabled = !isBusy;
        LaunchProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private void ShowPage(FrameworkElement page, Button active)
    {
        foreach (var p in new FrameworkElement[] { HomePage, ModrinthPage, ReportsPage, StatusPage, NewsPage, SettingsPage }) p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new[] { HomeNavButton, InstancesNavButton, ModsNavButton, ReportsNavButton, StatusNavButton, NewsNavButton, SettingsNavButton }) b.Background = ReferenceEquals(b, active) ? (Brush)FindResource("SidebarActiveBrush") : Brushes.Transparent;
        if (ReferenceEquals(page, ReportsPage)) RefreshReports();
        if (ReferenceEquals(page, StatusPage)) _ = RefreshServiceStatusesAsync();
        if (ReferenceEquals(page, NewsPage)) _ = RefreshNewsAsync();
        Dispatcher.BeginInvoke(new Action(() => _ = RefreshDiscordPresenceAsync()), DispatcherPriority.Background);
    }

    private void InitializeExtras()
    {
        _loadingSettings = true;
        ThemeBox.SelectedIndex = _preferences.LightTheme ? 1 : 0;
        foreach (ComboBoxItem item in MemoryBox.Items) if (int.TryParse(item.Tag?.ToString(), out var ram) && ram == _preferences.MemoryMb) MemoryBox.SelectedItem = item;
        if (MemoryBox.SelectedItem is null) MemoryBox.SelectedIndex = 1;
        foreach (ComboBoxItem item in MinimumMemoryBox.Items) if (int.TryParse(item.Tag?.ToString(), out var ram) && ram == _preferences.MinimumMemoryMb) MinimumMemoryBox.SelectedItem = item;
        if (MinimumMemoryBox.SelectedItem is null) MinimumMemoryBox.SelectedIndex = 1;
        if (_preferences.MinimumMemoryMb > _preferences.MemoryMb) _preferences.MinimumMemoryMb = Math.Min(1024, _preferences.MemoryMb);
        JavaPathBox.Text = _preferences.JavaPath;
        JavaPathStatus.Text = string.IsNullOrWhiteSpace(_preferences.JavaPath) ? "Java automático" : $"Java manual: {_preferences.JavaPath}";
        ExtraJvmArgumentsBox.Text = _preferences.ExtraJvmArguments;
        FullScreenBox.IsChecked = _preferences.FullScreen;
        LauncherPresenceBox.IsChecked = _preferences.LauncherRichPresenceEnabled;
        MinecraftPresenceBox.IsChecked = _preferences.MinecraftRichPresenceEnabled;
        if (!string.IsNullOrWhiteSpace(NewsFeedConfiguration.DefaultFeedUrl))
        {
            _preferences.NewsFeedUrl = NewsFeedConfiguration.DefaultFeedUrl;
            LauncherExtrasStore.SavePreferences(_preferences);
        }
        var configuredFeedIsWebhook = IsDiscordWebhookUrl(_preferences.NewsFeedUrl);
        if (configuredFeedIsWebhook)
        {
            _preferences.NewsFeedUrl = string.Empty;
            LauncherExtrasStore.SavePreferences(_preferences);
        }
        if (configuredFeedIsWebhook)
            NewsStatus.Text = "Esse endereço é um webhook de envio do Discord. Para receber mensagens, use o feed HTTPS publicado pelo bot.";
        RefreshMicrosoftAccounts();
        _loadingSettings = false;
        ApplyTheme(_preferences.LightTheme);
        _newsTimer.Start();
        _discordPresenceTimer.Start();
        _ = RefreshDiscordPresenceAsync();
        RefreshReports();
        RefreshHomeDashboard();
    }


    private void ApplyTheme(bool light)
    {
        var resources = Application.Current.Resources;
        SetThemeBrush(resources, "WindowBrush", light ? Color.FromRgb(246,246,248) : Color.FromRgb(10,9,11));
        SetThemeBrush(resources, "TextBrush", light ? Color.FromRgb(38,35,39) : Color.FromRgb(244,242,243));
        SetThemeBrush(resources, "SidebarBrush", light ? Color.FromRgb(25,20,23) : Color.FromRgb(16,13,15));
        SetThemeBrush(resources, "SidebarTextBrush", Color.FromRgb(244,246,248));
        SetThemeBrush(resources, "SidebarMutedBrush", Color.FromRgb(170,178,191));
        SetThemeBrush(resources, "SidebarActiveBrush", light ? Color.FromRgb(91,27,43) : Color.FromRgb(43,23,28));
        SetThemeBrush(resources, "PanelBrush", light ? Colors.White : Color.FromRgb(21,18,20));
        SetThemeBrush(resources, "SubPanelBrush", light ? Color.FromRgb(250,247,248) : Color.FromRgb(28,23,25));
        SetThemeBrush(resources, "ResultBrush", light ? Color.FromRgb(244,241,243) : Color.FromRgb(32,25,28));
        SetThemeBrush(resources, "IconBrush", light ? Color.FromRgb(231,225,228) : Color.FromRgb(56,39,44));
        SetThemeBrush(resources, "MutedBrush", light ? Color.FromRgb(91,83,88) : Color.FromRgb(185,174,178));
        SetThemeBrush(resources, "ControlBrush", light ? Colors.White : Color.FromRgb(27,23,25));
        SetThemeBrush(resources, "InputBrush", light ? Colors.White : Color.FromRgb(16,13,15));
        SetThemeBrush(resources, "BorderBrush", light ? Color.FromRgb(215,207,211) : Color.FromRgb(58,43,48));
        SetThemeBrush(resources, "DropDownBrush", light ? Colors.White : Color.FromRgb(23,19,21));
        SetThemeBrush(resources, "HoverBrush", light ? Color.FromRgb(249,232,236) : Color.FromRgb(43,26,32));
        SetThemeBrush(resources, "SelectedBrush", light ? Color.FromRgb(255,225,232) : Color.FromRgb(74,23,34));
        SetThemeBrush(resources, "SecondaryButtonBrush", light ? Color.FromRgb(239,230,233) : Color.FromRgb(43,32,36));
        Background = (Brush)resources["WindowBrush"];
        Foreground = (Brush)resources["TextBrush"];
        foreach (var element in new FrameworkElement[] { HomePage, ModrinthPage, ReportsPage, NewsPage, SettingsPage })
            element.InvalidateVisual();
    }

    private static void SetThemeBrush(ResourceDictionary resources, string key, Color color)
    {
        resources[key] = new SolidColorBrush(color);
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeBox.SelectedIndex < 0) return;
        _preferences.LightTheme = ThemeBox.SelectedIndex == 1; ApplyTheme(_preferences.LightTheme); LauncherExtrasStore.SavePreferences(_preferences);
    }

    private void Memory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || MemoryBox.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out var ram)) return;
        _preferences.MemoryMb = ram;
        if (_preferences.MinimumMemoryMb > ram)
        {
            var adjustedMinimum = MinimumMemoryBox.Items.OfType<ComboBoxItem>().LastOrDefault(option => int.TryParse(option.Tag?.ToString(), out var value) && value <= ram);
            if (adjustedMinimum is not null)
            {
                _loadingSettings = true;
                MinimumMemoryBox.SelectedItem = adjustedMinimum;
                _preferences.MinimumMemoryMb = int.Parse(adjustedMinimum.Tag!.ToString()!);
                _loadingSettings = false;
            }
        }
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private void MinimumMemory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || MinimumMemoryBox.SelectedItem is not ComboBoxItem item || !int.TryParse(item.Tag?.ToString(), out var ram)) return;
        var max = _preferences.MemoryMb;
        if (ram > max)
        {
            var adjusted = MinimumMemoryBox.Items.OfType<ComboBoxItem>().LastOrDefault(option => int.TryParse(option.Tag?.ToString(), out var value) && value <= max);
            if (adjusted is not null)
            {
                _loadingSettings = true;
                MinimumMemoryBox.SelectedItem = adjusted;
                _loadingSettings = false;
                ram = int.Parse(adjusted.Tag!.ToString()!);
            }
        }
        _preferences.MinimumMemoryMb = ram;
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private void SuggestMemory_Click(object sender, RoutedEventArgs e)
    {
        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        var targetMb = Math.Clamp(totalMb / 2, 2048, 8192);
        var option = MemoryBox.Items.OfType<ComboBoxItem>()
            .Where(item => int.TryParse(item.Tag?.ToString(), out var value) && value <= targetMb)
            .LastOrDefault() ?? MemoryBox.Items.OfType<ComboBoxItem>().First();
        MemoryBox.SelectedItem = option;
        _preferences.MinimumMemoryMb = 1024;
        var minimum = MinimumMemoryBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "1024");
        if (minimum is not null) MinimumMemoryBox.SelectedItem = minimum;
        LauncherExtrasStore.SavePreferences(_preferences);
        MessageBox.Show($"Sugestão aplicada: {_preferences.MemoryMb / 1024.0:0.#} GB para o jogo, reservando memória para o Windows e outros aplicativos.", "Memória sugerida", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Fullscreen_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _preferences.FullScreen = FullScreenBox.IsChecked == true;
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private void JavaPath_LostFocus(object sender, RoutedEventArgs e) => SaveJavaPath(JavaPathBox.Text.Trim());

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher Java para o Minecraft",
            Filter = "Executável Java (javaw.exe;java.exe)|javaw.exe;java.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) SaveJavaPath(dialog.FileName);
    }

    private void ResetJava_Click(object sender, RoutedEventArgs e) => SaveJavaPath(string.Empty);

    private void SaveJavaPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && (!File.Exists(path) || !Path.GetFileName(path).Equals("java.exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)))
        {
            JavaPathStatus.Text = "Escolha um java.exe ou javaw.exe existente.";
            return;
        }
        _preferences.JavaPath = path;
        JavaPathBox.Text = path;
        JavaPathStatus.Text = string.IsNullOrWhiteSpace(path) ? "Java automático" : $"Java manual: {path}";
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private void ExtraJvmArguments_LostFocus(object sender, RoutedEventArgs e)
    {
        _preferences.ExtraJvmArguments = ExtraJvmArgumentsBox.Text.Trim();
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private async void DiscordPresence_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _preferences.LauncherRichPresenceEnabled = LauncherPresenceBox.IsChecked == true;
        _preferences.MinecraftRichPresenceEnabled = MinecraftPresenceBox.IsChecked == true;
        LauncherExtrasStore.SavePreferences(_preferences);
        await RefreshDiscordPresenceAsync();
    }

    private async Task RefreshDiscordPresenceAsync()
    {
        if (DiscordPresenceStatus is null || _loadingSettings) return;
        var launcherEnabled = _preferences.LauncherRichPresenceEnabled;
        var minecraftEnabled = _preferences.MinecraftRichPresenceEnabled;
        var runningGame = minecraftEnabled && !string.IsNullOrWhiteSpace(_runningVersion);
        if (!launcherEnabled && !minecraftEnabled)
        {
            await ClearDiscordPresenceAsync();
            DiscordPresenceStatus.Text = "Rich Presence desativado.";
            return;
        }

        var applicationId = DiscordPresenceConfiguration.ApplicationId.Trim();
        if (applicationId.Length is < 17 or > 20 || applicationId.Any(character => !char.IsAsciiDigit(character)))
        {
            await ClearDiscordPresenceAsync();
            DiscordPresenceStatus.Text = "Rich Presence ainda não foi configurado para este launcher.";
            return;
        }

        string details;
        string state;
        long? start = null;
        if (runningGame)
        {
            details = $"Jogando Minecraft Java {_runningVersion}";
            state = "Iniciado pelo SAPVP-AI";
            start = _gameStartedUnix;
        }
        else if (launcherEnabled && GetLauncherPresence() is { } launcherPresence)
        {
            (details, state) = launcherPresence;
        }
        else
        {
            await ClearDiscordPresenceAsync();
            DiscordPresenceStatus.Text = minecraftEnabled
                ? "A atividade do Minecraft aparecerá quando iniciar um jogo pelo launcher."
                : "Atividade ocultada fora de Início, Biblioteca de mods e Instâncias.";
            return;
        }

        try
        {
            await _discordPresence.SetActivityAsync(applicationId, details, state, start);
            DiscordPresenceStatus.Text = runningGame
                ? "Discord mostra o Minecraft em execução."
                : "Discord mostra a seção atual do SAPVP-AI.";
        }
        catch (Exception ex)
        {
            DiscordPresenceStatus.Text = ex.Message.Contains("Discord Desktop", StringComparison.OrdinalIgnoreCase)
                ? "Abra o Discord Desktop para publicar a atividade."
                : "Rich Presence indisponível: " + ex.Message;
        }
    }

    private (string Details, string State)? GetLauncherPresence()
    {
        if (HomePage.Visibility == Visibility.Visible) return ("Na tela inicial", "Escolhendo uma versão · SAPVP-AI");
        if (ModrinthPage.Visibility != Visibility.Visible) return null;
        if (LibraryInstancesView.Visibility == Visibility.Visible) return ("Gerenciando instâncias", "Mods e versões · SAPVP-AI");
        if (ModDetailsView.Visibility == Visibility.Visible) return ("Na biblioteca de mods", "Vendo detalhes de um projeto · SAPVP-AI");
        if (LibraryTabs.SelectedItem == ExploreModsTab) return ("Na biblioteca de mods", "Explorando mods · SAPVP-AI");
        if (LibraryTabs.SelectedItem == ResourcePacksTab) return ("Na biblioteca de texturas", "Explorando resource packs · SAPVP-AI");
        if (LibraryTabs.SelectedItem == ShadersTab) return ("Na biblioteca de shaders", "Explorando shaders · SAPVP-AI");
        return null;
    }

    private async Task ClearDiscordPresenceAsync()
    {
        try { await _discordPresence.ClearActivityAsync(); }
        catch { }
    }

    private async Task RefreshNewsAsync()
    {
        if (string.IsNullOrWhiteSpace(_preferences.NewsFeedUrl) || _refreshingNews) return;
        if (IsDiscordWebhookUrl(_preferences.NewsFeedUrl))
        {
            NewsStatus.Text = "Webhook detectado. Configure a URL do feed HTTPS publicado pelo bot.";
            return;
        }
        _refreshingNews = true;
        try
        {
            var json = await NewsHttpClient.GetStringAsync(_preferences.NewsFeedUrl);
            var news = JsonSerializer.Deserialize<List<NewsItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            _lastNewsCount = news.Count;
            RefreshHomeDashboard();
            CleanupReadNews();
            _newsCards = news
                .Where(item => string.IsNullOrWhiteSpace(item.Id) ||
                    !_preferences.ReadNewsAt.TryGetValue(item.Id, out var readAt) ||
                    DateTime.UtcNow - readAt.ToUniversalTime() < TimeSpan.FromHours(4))
                .OrderByDescending(item => item.PublishedAt)
                .Take(50)
                .Select(item => new NewsCard(item, !string.IsNullOrWhiteSpace(item.Id) && _preferences.ReadNewsAt.ContainsKey(item.Id)))
                .ToList();
            ApplyNewsFilter();
            NewsStatus.Text = $"Feed atualizado às {DateTime.Now:HH:mm:ss}";
        }
        catch
        {
            NewsStatus.Text = "Não foi possível atualizar agora · confira sua conexão";
            if (_newsCards.Count == 0)
            {
                NewsEmptyTitle.Text = "Não foi possível carregar";
                NewsEmptyDescription.Text = "Confira sua conexão e tente atualizar novamente.";
                NewsEmptyPanel.Visibility = Visibility.Visible;
            }
        }
        finally { _refreshingNews = false; }
    }

    private void ApplyNewsFilter()
    {
        if (NewsList is null) return;
        var filter = NewsFilterBox?.SelectedIndex ?? 0;
        var visible = _newsCards.Where(card => filter switch
        {
            1 => !card.IsRead,
            2 => card.IsRead,
            _ => true,
        }).ToList();
        NewsList.ItemsSource = visible;
        var unreadCount = _newsCards.Count(card => !card.IsRead);
        NewsCountLabel.Text = _newsCards.Count == 0
            ? "Acompanhe avisos e atualizações do projeto"
            : $"{_newsCards.Count} novidade(s) · {unreadCount} não lida(s)";
        NewsEmptyPanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_newsCards.Count == 0)
        {
            NewsEmptyTitle.Text = "Ainda sem novidades";
            NewsEmptyDescription.Text = "Quando houver avisos e atualizações do projeto, eles aparecerão aqui.";
        }
        else if (visible.Count == 0)
        {
            NewsEmptyTitle.Text = filter == 1 ? "Tudo em dia" : "Nenhuma novidade lida";
            NewsEmptyDescription.Text = filter == 1
                ? "Você já conferiu todas as novidades. As próximas aparecem aqui automaticamente."
                : "As novidades que você marcar como lidas ficam disponíveis por mais 4 horas.";
        }
    }

    private async void RefreshNews_Click(object sender, RoutedEventArgs e) => await RefreshNewsAsync();
    private void NewsFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyNewsFilter();

    private void MarkNewsRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not NewsCard card || string.IsNullOrWhiteSpace(card.Item.Id)) return;
        _preferences.ReadNewsAt[card.Item.Id] = DateTime.UtcNow;
        LauncherExtrasStore.SavePreferences(_preferences);
        card.MarkRead();
        button.Content = card.ReadLabel;
        NewsStatus.Text = "Novidade marcada como lida. Ela será ocultada após 4 horas.";
        ApplyNewsFilter();
    }

    private void CleanupReadNews()
    {
        if (DateTime.UtcNow - _lastReadNewsCleanup < TimeSpan.FromHours(1)) return;
        _lastReadNewsCleanup = DateTime.UtcNow;
        foreach (var id in _preferences.ReadNewsAt
            .Where(pair => DateTime.UtcNow - pair.Value.ToUniversalTime() > TimeSpan.FromDays(14))
            .Select(pair => pair.Key).ToList())
            _preferences.ReadNewsAt.Remove(id);
        LauncherExtrasStore.SavePreferences(_preferences);
    }

    private sealed class NewsCard
    {
        public NewsCard(NewsItem item, bool isRead)
        {
            Item = item;
            IsRead = isRead;
            ReadLabel = isRead ? "Lida · ocultar em 4h" : "Marcar como lida";
        }

        public NewsItem Item { get; }
        public string Title => Item.Title;
        public string Message => string.Equals((Item.Message ?? string.Empty).Trim(), Item.Title.Trim(), StringComparison.OrdinalIgnoreCase) ? string.Empty : Item.Message ?? string.Empty;
        public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
        public bool IsRead { get; private set; }
        public Visibility BadgeVisibility => IsRead ? Visibility.Collapsed : Visibility.Visible;
        public string ReadStateLabel => IsRead ? "Lida · disponível por mais 4 horas" : "Ainda não lida";
        public string PublishedAtLabel => Item.PublishedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Data desconhecida";
        public string ReadLabel { get; private set; }
        public void MarkRead()
        {
            IsRead = true;
            ReadLabel = "Lida · ocultar em 4h";
        }
    }

    private static bool IsDiscordWebhookUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)) &&
            uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportsNav_Click(object sender, RoutedEventArgs e) => ShowPage(ReportsPage, ReportsNavButton);
    private void StatusNav_Click(object sender, RoutedEventArgs e) => ShowPage(StatusPage, StatusNavButton);
    private void NewsNav_Click(object sender, RoutedEventArgs e) => ShowPage(NewsPage, NewsNavButton);
    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage, SettingsNavButton);
        _ = RefreshAppearanceAsync(showErrors: false);
    }

    private async void CheckLauncherUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateLauncherButton.IsEnabled = false;
        LauncherUpdateStatus.Text = "Consultando GitHub…";
        try
        {
            var release = await _launcherUpdateService.GetLatestReleaseAsync();
            if (release is null) return;
            if (release.Version <= LauncherUpdateService.CurrentVersion)
            {
                LauncherUpdateStatus.Text = $"Você já está na versão {LauncherUpdateService.CurrentVersion}.";
                return;
            }

            var notes = string.IsNullOrWhiteSpace(release.Notes) ? "Sem notas nesta publicação." : release.Notes.Trim();
            if (notes.Length > 700) notes = notes[..700] + "…";
            var answer = MessageBox.Show(
                $"Está disponível a versão {release.Version} (atual: {LauncherUpdateService.CurrentVersion}).\n\n{notes}\n\nDeseja baixar e abrir o instalador? O download será verificado antes de abrir.",
                "Atualização do launcher", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                LauncherUpdateStatus.Text = $"Versão {release.Version} disponível.";
                return;
            }

            LauncherUpdateStatus.Text = "Baixando e verificando o instalador…";
            await _launcherUpdateService.DownloadAndOpenInstallerAsync(release);
            MessageBox.Show("O instalador foi verificado e aberto. Siga as etapas para atualizar; o launcher será fechado agora.",
                "Instalador aberto", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LauncherUpdateStatus.Text = "Não foi possível consultar ou baixar a atualização.";
            MessageBox.Show(ex.Message, "Atualização do launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateLauncherButton.IsEnabled = true;
        }
    }

    private void RefreshReports()
    {
        if (ReportsList is null) return;
        ReportsList.ItemsSource = LauncherExtrasStore.LoadSessions().Select(s => $"{s.StartedAt:dd/MM/yyyy HH:mm}  •  Minecraft {s.Version} ({s.Loader})  •  {s.Duration:hh\\:mm\\:ss}  •  {s.Result} (código {s.ExitCode})" + (string.IsNullOrWhiteSpace(s.Diagnosis) ? "" : $"\n   Diagnóstico: {s.Diagnosis}")).ToList();
    }

    private void ValidateMods_Click(object sender, RoutedEventArgs e)
    {
        if (VersionSelector.SelectedItem is not string version) return;
        var folder = Path.Combine(GetCurrentInstancePath(version), "mods"); Directory.CreateDirectory(folder); var issues = new List<string>(); var checkedCount = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.jar", System.IO.SearchOption.TopDirectoryOnly))
        {
            checkedCount++;
            try { using var archive = ZipFile.OpenRead(file); if (archive.Entries.Count == 0) issues.Add($"{Path.GetFileName(file)}: arquivo JAR vazio."); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { issues.Add($"{Path.GetFileName(file)}: JAR inválido ou corrompido."); }
        }
        var managed = ModrinthService.LoadInstalledMods(folder);
        foreach (var mod in managed.Where(m => !string.IsNullOrWhiteSpace(m.GameVersion) && !string.Equals(m.GameVersion, version, StringComparison.OrdinalIgnoreCase)))
            issues.Add($"{mod.Name}: marcado para Minecraft {mod.GameVersion}, instância atual é {version}.");
        ValidationSummary.Text = issues.Count == 0 ? $"Validação concluída: {checkedCount} JAR(s), sem problemas detectados. Mods externos têm compatibilidade não confirmada." : $"{issues.Count} problema(s) em {checkedCount} JAR(s).";
        MessageBox.Show(issues.Count == 0 ? ValidationSummary.Text : string.Join(Environment.NewLine, issues.Take(25)), "Validador de mods", MessageBoxButton.OK, issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}

public sealed record ModInstallTarget(string Label, string Version)
{
    public override string ToString() => Label;
}
