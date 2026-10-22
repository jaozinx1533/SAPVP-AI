namespace MinecraftProjectLauncher.Services;

/// <summary>
/// Feed URL baked into launcher releases. Set this to the project's stable HTTPS feed before
/// distributing a release. Players cannot change this value from the launcher UI.
/// </summary>
public static class NewsFeedConfiguration
{
    public const string DefaultFeedUrl = "https://minecraft-launcher-news.sapvpai.workers.dev/news.json";
}
