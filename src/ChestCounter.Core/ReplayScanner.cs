namespace ChestCounter;

public sealed class RunReport
{
    public int Total;
    public int Parsed;
    public int Failed;
    public int NewMatches;
    public int AlreadyCounted;
    public int SkippedMode;      // parsed fine but not a counted playlist (ranked / reload / creative / …)
    public int ActiveRecording;  // match currently in progress (will be parsed as soon as Fortnite finishes it)
    public int AbandonedCrash;   // old incomplete replay where Fortnite crashed before writing the AES key
    public int RescuedViaOcr;    // crashed match whose chests were rescued from LiveSessionJournal
    public int UpgradedFromLive; // Play Again match upgraded in-place with bit-accurate replay details
    internal readonly List<string> SettledFiles = new();
    public string? ActiveMatchFile;
}

public sealed class ReplayScanner
{
    // Only public Battle Royale counts for Treasure Hunters: normal Build (Playlist_Default*)
    // and Zero Build (Playlist_NoBuildBR_*).
    private static readonly HashSet<string> CountedPlaylists = new(StringComparer.OrdinalIgnoreCase)
    {
        "Playlist_DefaultSolo", "Playlist_DefaultDuo", "Playlist_DefaultTrio", "Playlist_DefaultSquad",
        "Playlist_NoBuildBR_Solo", "Playlist_NoBuildBR_Duo", "Playlist_NoBuildBR_Trio", "Playlist_NoBuildBR_Squad",
    };

    private static readonly TimeSpan ActiveReplayThreshold = TimeSpan.FromMinutes(90);

    public static bool IsCountedPlaylist(string? playlist) =>
        playlist != null && CountedPlaylists.Contains(playlist);

    public static string FileKey(string file)
    {
        try { var info = new FileInfo(file); return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; }
        catch { return Path.GetFileName(file); }
    }

    internal enum OutcomeKind
    {
        Counted,
        SkippedMode,
        ActiveRecording,
        AbandonedIncomplete,
        Failed,
    }

    internal sealed record ScanItem(
        int Index,
        string FilePath,
        string Key,
        DateTime LastWriteUtc,
        OutcomeKind Kind,
        MatchChestCounts? Counts,
        string? Error);

    internal sealed record Batch(RunReport Report, ScanItem[] Items);

    // Multi-core parallel replay scanner: parses all unscanned replays concurrently across CPU cores,
    // attributes every chest (searched, broken, floor-broken, vehicle-broken, buried, relic, refilled),
    // and folds matches deterministically (oldest-first) into LifetimeStats.
    public RunReport Scan(IReadOnlyList<string> files, LifetimeStats stats,
        Action<int, int, string, string?>? onProgress = null) =>
        Apply(Prepare(files, stats, onProgress), stats);

    internal static Batch Prepare(IReadOnlyList<string> files, LifetimeStats stats,
        Action<int, int, string, string?>? onProgress)
    {
        var run = new RunReport { Total = files.Count };

        var toParse = new List<(int Index, string FilePath, string Key, DateTime LastWriteUtc)>();
        int preDone = 0;

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string key = FileKey(file);

            if (stats.ScannedFiles.Contains(key))
            {
                run.AlreadyCounted++;
                preDone++;
                onProgress?.Invoke(preDone, files.Count, Path.GetFileName(file), null);
                continue;
            }

            if (stats.AbandonedFiles.Contains(key))
            {
                run.AbandonedCrash++;
                preDone++;
                onProgress?.Invoke(preDone, files.Count, Path.GetFileName(file), null);
                continue;
            }

            DateTime lastWriteUtc;
            try { lastWriteUtc = File.GetLastWriteTimeUtc(file); }
            catch { lastWriteUtc = DateTime.UtcNow; }

            toParse.Add((i, file, key, lastWriteUtc));
        }

        if (toParse.Count == 0) return new Batch(run, []);

        var results = new ScanItem[toParse.Count];
        int completed = preDone;
        var nowUtc = DateTime.UtcNow;

        Parallel.For(0, toParse.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 6)
        }, idx =>
        {
            var work = toParse[idx];
            ScanItem item;
            try
            {
                var reader = new ChestScanReader();
                using var stream = new FileStream(work.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var replay = reader.ReadReplay(stream);

                if (reader.IsPartialReplay || replay.Info?.IsLive == true)
                {
                    bool recent = nowUtc - work.LastWriteUtc <= ActiveReplayThreshold;
                    item = new ScanItem(
                        work.Index, work.FilePath, work.Key, work.LastWriteUtc,
                        recent ? OutcomeKind.ActiveRecording : OutcomeKind.AbandonedIncomplete,
                        null, null);
                }
                else if (string.IsNullOrWhiteSpace(replay.GameData?.CurrentPlaylist))
                    throw new InvalidDataException("O replay não identificou o modo da partida.");
                else if (!IsCountedPlaylist(replay.GameData?.CurrentPlaylist))
                {
                    var skippedCounts = new MatchChestCounts
                    {
                        MatchId = replay.GameData?.GameSessionId ?? "",
                        Playlist = replay.GameData?.CurrentPlaylist ?? "",
                    };
                    item = new ScanItem(
                        work.Index, work.FilePath, work.Key, work.LastWriteUtc,
                        OutcomeKind.SkippedMode, skippedCounts, null);
                }
                else
                {
                    var counts = reader.Classify(replay);
                    if (string.IsNullOrEmpty(counts.MatchId))
                        throw new InvalidDataException("O replay não identificou a partida.");
                    if (counts.Time == DateTime.MinValue) counts.Time = work.LastWriteUtc;
                    item = new ScanItem(
                        work.Index, work.FilePath, work.Key, work.LastWriteUtc,
                        OutcomeKind.Counted, counts, null);
                }
            }
            catch (Exception ex)
            {
                // Unreal.Core throws "Replay is marked encrypted but not yet marked as completed!"
                // when Fortnite has not yet written the AES key to the header.
                bool isLiveOrUnfinished = ex.Message.Contains("not yet marked as completed", StringComparison.OrdinalIgnoreCase)
                                          || ex.Message.Contains("still being recorded", StringComparison.OrdinalIgnoreCase);
                if (isLiveOrUnfinished)
                {
                    bool recent = nowUtc - work.LastWriteUtc <= ActiveReplayThreshold;
                    item = new ScanItem(
                        work.Index, work.FilePath, work.Key, work.LastWriteUtc,
                        recent ? OutcomeKind.ActiveRecording : OutcomeKind.AbandonedIncomplete,
                        null, null);
                }
                else
                {
                    item = new ScanItem(
                        work.Index, work.FilePath, work.Key, work.LastWriteUtc,
                        OutcomeKind.Failed, null, ex.Message);
                }
            }

            if (work.Key != FileKey(work.FilePath))
                item = item with { Kind = OutcomeKind.ActiveRecording, Counts = null, Error = null };
            results[idx] = item;
            int doneNow = Interlocked.Increment(ref completed);
            onProgress?.Invoke(doneNow, files.Count, Path.GetFileName(work.FilePath), item.Error);
        });

        return new Batch(run, results);
    }

    internal static RunReport Apply(Batch batch, LifetimeStats stats)
    {
        var run = batch.Report;
        if (batch.Items.Length == 0) return run;
        var liveJournal = LiveSessionJournal.Load();

        // Fold results deterministically from oldest file to newest file so match history stays ordered.
        foreach (var item in batch.Items.OrderBy(r => r.LastWriteUtc))
        {
            if (stats.ScannedFiles.Contains(item.Key))
            {
                run.AlreadyCounted++;
                continue;
            }
            if (stats.AbandonedFiles.Contains(item.Key))
            {
                run.AbandonedCrash++;
                continue;
            }
            string fileName = Path.GetFileName(item.FilePath);
            switch (item.Kind)
            {
                case OutcomeKind.Counted:
                    run.Parsed++;
                    stats.ScannedFiles.Add(item.Key);
                    bool wasPlayAgainPending = stats.Records.Any(r =>
                        r.Source == "play-again"
                        && (string.Equals(r.MatchId, item.Counts!.MatchId, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(r.ReplayFile) && string.Equals(r.ReplayFile, fileName, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(r.MatchId, "play-again:" + fileName, StringComparison.OrdinalIgnoreCase)));
                    if (stats.AddMatch(item.Counts!, "replay", fileName))
                    {
                        run.NewMatches++;
                    }
                    else
                    {
                        run.AlreadyCounted++;
                        if (wasPlayAgainPending) run.UpgradedFromLive++;
                    }
                    run.SettledFiles.Add(fileName);
                    break;

                case OutcomeKind.SkippedMode:
                    run.Parsed++;
                    stats.ScannedFiles.Add(item.Key);
                    stats.RemoveEstimates(fileName, item.Counts?.MatchId);
                    run.SkippedMode++;
                    run.SettledFiles.Add(fileName);
                    break;

                case OutcomeKind.ActiveRecording:
                    run.ActiveRecording++;
                    run.ActiveMatchFile = item.FilePath; // Results are folded oldest first.
                    break;

                case OutcomeKind.AbandonedIncomplete:
                    run.AbandonedCrash++;
                    stats.AbandonedFiles.Add(item.Key);
                    // If Fortnite crashed mid-match (and wasn't already finalized via Play Again),
                    // rescue any chests captured live by OCR during that match!
                    if (liveJournal != null
                        && !liveJournal.FinalizedViaPlayAgain
                        && (string.IsNullOrWhiteSpace(liveJournal.Playlist) || IsCountedPlaylist(liveJournal.Playlist))
                        && !stats.HasReplayRecord(fileName, liveJournal.SessionId)
                        && string.Equals(liveJournal.ReplayFileName, fileName, StringComparison.OrdinalIgnoreCase)
                        && liveJournal.LiveChests > 0)
                    {
                        var rescued = new MatchChestCounts
                        {
                            MatchId = !string.IsNullOrEmpty(liveJournal.SessionId) ? liveJournal.SessionId : ("ocr-rescue:" + fileName),
                            Time = liveJournal.StartedUtc,
                            You = liveJournal.LiveChests,
                            Playlist = !string.IsNullOrEmpty(liveJournal.Playlist) ? liveJournal.Playlist : "OCR Rescue",
                        };
                        if (stats.AddMatch(rescued, "ocr-rescue", fileName))
                        {
                            run.NewMatches++;
                            run.RescuedViaOcr++;
                        }
                        run.SettledFiles.Add(fileName);
                    }
                    break;

                case OutcomeKind.Failed:
                    run.Failed++;
                    break;
            }
        }

        return run;
    }
}
