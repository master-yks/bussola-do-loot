namespace ChestCounter;

// High-level entry point the GUI and live counter share.
public static class ChestCounterService
{
    // Default Fortnite replay folder on this machine.
    public static string DefaultDemosPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortniteGame", "Saved", "Demos");

    public static string StatsPath => StatsStore.DefaultPath;

    // Start of the current (pirate / Treasure Hunters) season.
    public static readonly DateTime SeasonStartUtc = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    // Official creator attribution shown across the app & overlays.
    public const string Credits = "master-yks";
    public const string Domain = "master-yks";

    // Compact stats as JSON for the stream overlay; `live` = this-match count from OCR.
    public static string OverlayJson(LifetimeStats s, int? live = null)
    {
        var season = s.Over(SeasonStartUtc);
        long seasonLiveTotal = season.YourChests + Math.Max(0, live ?? 0);
        double tokenProb = TreasureCatalog.ChanceOfAtLeastOne(137_500, seasonLiveTotal);
        string tokenChance = TreasureCatalog.FormatChance(tokenProb, highPrecision: true);

        int best = 0;
        MatchRecord? latest = null;
        foreach (var record in s.Records)
        {
            best = Math.Max(best, record.You);
            if (latest is null || record.Time >= latest.Time) latest = record;
        }
        int? last = latest?.You;
        var now = DateTime.UtcNow;
        var th = TreasureCatalog.CurrentOrNext(now);
        bool thLive = th != null && TreasureCatalog.IsLive(th, now);
        string thText = "";
        if (th != null)
        {
            var rem = (thLive ? th.EndUtc : th.StartUtc) - now;
            if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
            string clock = rem.TotalHours >= 24
                ? $"{(int)rem.TotalDays}d {rem.Hours:00}h"
                : $"{(int)rem.TotalHours:00}:{rem.Minutes:00}:{rem.Seconds:00}";
            thText = thLive ? $"★ {clock}" : clock;
        }

        var dto = new
        {
            live,
            avg = season.Matches > 0 ? season.AvgYou : s.AvgYou,
            total = seasonLiveTotal,
            seasonMatches = season.Matches,
            matches = s.Matches,
            best,
            last,
            tokenChance,
            treasureHour = thText,
            treasureHourLive = thLive,
            domain = Credits,
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
    }

    public static LifetimeStats LoadStats() => StatsStore.Load(StatsPath);

    // The GUI and the headless counter may finish scanning the same match concurrently.
    private static readonly Mutex StatsWriter = new(false, $@"Local\{AppChannel.Name}StatsWriter");
    private static readonly object ScanLock = new();

    private static T WithWriter<T>(Func<T> action)
    {
        try { StatsWriter.WaitOne(); }
        catch (AbandonedMutexException) { /* Previous process exited; this thread now owns the mutex. */ }
        try { return action(); }
        finally { StatsWriter.ReleaseMutex(); }
    }

    public static void Save(LifetimeStats stats) => WithWriter(() =>
    {
        StatsStore.Save(StatsPath, stats);
        return true;
    });

    public static bool DemosFolderExists(string? demos = null) =>
        Directory.Exists(demos ?? DefaultDemosPath);

    // All replay files in `demos`, newest first.
    public static List<string> FindReplays(string? demos = null)
    {
        demos ??= DefaultDemosPath;
        if (!Directory.Exists(demos)) return new();
        try
        {
            return Directory.GetFiles(demos, "*.replay")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).ToList();
        }
        catch
        {
            return new();
        }
    }

    // Scan the given replays in parallel, fold new matches into the saved lifetime stats, persist,
    // and return both the run summary and the updated lifetime stats.
    public static (RunReport run, LifetimeStats stats) Scan(
        IReadOnlyList<string> files, Action<int, int, string, string?>? onProgress = null)
    {
        lock (ScanLock)
        {
            // Play Again can save while a season's worth of replays is being decoded.
            var batch = ReplayScanner.Prepare(files, LoadStats(), onProgress);
            return WithWriter(() =>
            {
                var stats = LoadStats();
                var run = ReplayScanner.Apply(batch, stats);
                if (run.Parsed > 0 || run.RescuedViaOcr > 0 || batch.Items.Any(i => i.Kind == ReplayScanner.OutcomeKind.AbandonedIncomplete))
                {
                    StatsStore.Save(StatsPath, stats);
                    foreach (var name in run.SettledFiles) LiveSessionJournal.Clear(name);
                }
                return (run, stats);
            });
        }
    }

    // Immediately finalizes and counts the active match the moment the player queues via "Play Again"
    // ("Jogar novamente") in Fortnite, before Fortnite stops the spectator replay driver. When the .replay
    // file later closes on server travel, ReplayScanner upgrades this exact record in-place with zero duplication.
    public static (RunReport run, LifetimeStats stats) FinalizePlayAgainMatch(
        string? sessionId,
        string? playlist,
        DateTime startedUtc,
        int liveChests,
        string? replayFileName) => WithWriter(() =>
    {
        var stats = LoadStats();
        var run = new RunReport { Total = 1, Parsed = 1 };

        if (!string.IsNullOrWhiteSpace(playlist) && !ReplayScanner.IsCountedPlaylist(playlist))
        {
            run.SkippedMode = 1;
            if (!string.IsNullOrWhiteSpace(replayFileName))
                LiveSessionJournal.Save(replayFileName, startedUtc, 0, sessionId, playlist, finalizedViaPlayAgain: true);
            else
                LiveSessionJournal.Clear();
            return (run, stats);
        }

        string matchId = !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId.Trim()
            : !string.IsNullOrWhiteSpace(replayFileName)
                ? "play-again:" + replayFileName
                : $"play-again:{startedUtc:O}";

        var counts = new MatchChestCounts
        {
            MatchId = matchId,
            Time = startedUtc != DateTime.MinValue ? startedUtc : DateTime.UtcNow,
            You = Math.Max(0, liveChests),
            Playlist = !string.IsNullOrWhiteSpace(playlist) ? playlist : "",
        };

        if (stats.AddMatch(counts, source: "play-again", replayFile: replayFileName))
            run.NewMatches = 1;
        else
            run.AlreadyCounted = 1;

        StatsStore.Save(StatsPath, stats);

        if (!string.IsNullOrWhiteSpace(replayFileName))
            LiveSessionJournal.Save(replayFileName, counts.Time, counts.You, sessionId, playlist, finalizedViaPlayAgain: true);
        else
            LiveSessionJournal.Clear();

        return (run, stats);
    });
}
