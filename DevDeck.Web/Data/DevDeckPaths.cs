namespace DevDeck.Web.Data;

public static class DevDeckPaths
{
    public static string RootFolder { get; } = Initialize();

    public static string DatabaseFile { get; } = Path.Combine(RootFolder, "devdeck.db");

    public static string LogsFolder { get; } = EnsureLogsFolder();

    public static string SqliteConnectionString => $"Data Source={DatabaseFile}";

    public static string LogFilePathFor(string serviceSlug, long runId, DateTimeOffset startedUtc)
    {
        var safe = Slugify(serviceSlug);
        var stamp = startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(LogsFolder, $"{safe}-{runId}-{stamp}.log");
    }

    public static string Slugify(string name)
    {
        var chars = name
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        return string.IsNullOrEmpty(slug) ? "service" : slug;
    }

    private static string Initialize()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        var folder = Path.Combine(appData, "DevDeck");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string EnsureLogsFolder()
    {
        var folder = Path.Combine(RootFolder, "logs");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
