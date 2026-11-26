using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter;

// One stored match: its id, start time, playlist mode, and how many chests you opened.
public sealed class MatchRecord
{
    public string MatchId { get; set; } = "";
    public DateTime Time { get; set; }   // match start (UTC); DateTime.MinValue if unknown
    public int You { get; set; }
    public Dictionary<string, int> YourChestKinds { get; set; } = new();
    public int CountOf(ChestKind kind) => YourChestKinds.GetValueOrDefault(kind.Key);
    [JsonIgnore]
    public int UntypedChests => You - ChestKinds.All.Sum(CountOf);
    public string Playlist { get; set; } = "";
    public int? Unattributed { get; set; }  // null for OCR and historical records without an audit
    public string Source { get; set; } = "replay";   // "replay", "play-again", or "ocr-rescue"

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplayFile { get; set; }

    // Squadmates' chests as seen from your replay. A minimum: a chest a teammate opens far away
    // from you never reaches your replay.
    public List<MateCount> Mates { get; set; } = new();

    // Treasure-hunt prizes pulled in this match by anyone in view.
    public List<PrizeFind> Prizes { get; set; } = new();

    [JsonIgnore]
    public int SquadVisibleChests => You + Mates.Sum(m => m.Chests);

    [JsonIgnore]
    public string SquadStatus => Playlist.EndsWith("Solo", StringComparison.OrdinalIgnoreCase) ? "Solo"
        : Source == "play-again" ? "Aguardando replay" : "Equipe sem dados";
}

// A teammate's totals across the matches you played together.
public readonly record struct MateTotal(string Name, int Matches, long Chests);

// A time-window aggregate.
public readonly record struct Period(int Matches, long YourChests, long SquadChests, double AvgYou);

// Chronological progression point for cumulative charts.
public readonly record struct CumulativePoint(
    int MatchNumber,
    DateTime TimeUtc,
    int MatchChests,
    long CumulativeYourChests,
    long CumulativeSquadChests);

// Crash-proof persistence for the currently active live match so even a mid-game crash never loses chests.
public sealed class LiveSessionState
{
    public string ReplayFileName { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public int LiveChests { get; set; }
    public string? SessionId { get; set; }
    public string? Playlist { get; set; }
    public bool FinalizedViaPlayAgain { get; set; }
}

public static class LiveSessionJournal
{
    private static readonly object Sync = new();

    public static string DefaultPath => Path.Combine(
        AppChannel.DataDirectory, "live_session.json");

    public static LiveSessionState? Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(DefaultPath))
                {
                    using var fs = File.OpenRead(DefaultPath);
                    return JsonSerializer.Deserialize<LiveSessionState>(fs);
                }
            }
            catch { }
            return null;
        }
    }

    public static void Save(
        string replayFileName,
        DateTime startedUtc,
        int liveChests,
        string? sessionId = null,
        string? playlist = null,
        bool finalizedViaPlayAgain = false)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
                var state = new LiveSessionState
                {
                    ReplayFileName = replayFileName,
                    StartedUtc = startedUtc,
                    LiveChests = liveChests,
                    SessionId = sessionId,
                    Playlist = playlist,
                    FinalizedViaPlayAgain = finalizedViaPlayAgain,
                };
                StatsStore.WriteAtomically(DefaultPath, JsonSerializer.Serialize(state));
            }
            catch { }
        }
    }

    public static void Clear(string? onlyIfFileName = null)
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(DefaultPath)) return;
                if (onlyIfFileName != null)
                {
                    var cur = JsonSerializer.Deserialize<LiveSessionState>(File.ReadAllText(DefaultPath));
                    if (cur != null && !string.Equals(cur.ReplayFileName, onlyIfFileName, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                File.Delete(DefaultPath);
            }
            catch { }
        }
    }
}

// Per-match history, persisted to disk and only ever appended to.
public sealed class LifetimeStats
{
    public const int CurrentVersion = 12;   // Reconcile provisional matches and replay observed closed-to-open transitions.

    public int Version { get; set; } = CurrentVersion;
    public List<MatchRecord> Records { get; set; } = new();

    // Keys (filename|size) of replays already parsed, so repeat scans skip them instantaneously.
    public HashSet<string> ScannedFiles { get; set; } = new();

    // Keys (filename|size) of old unencrypted crash replays where Fortnite exited before writing the AES key.
    public HashSet<string> AbandonedFiles { get; set; } = new();

    [JsonIgnore] private HashSet<string>? _ids;

    // Whole-history convenience accessors.
    [JsonIgnore] public int Matches => Records.Count;
    [JsonIgnore] public long YourChests => Records.Sum(r => (long)r.You);
    [JsonIgnore] public long SquadChests => Records.Sum(r => (long)r.SquadVisibleChests);
    [JsonIgnore] public double AvgYou => Records.Count > 0 ? (double)YourChests / Records.Count : 0;

    // Add a match, or refresh/upgrade one that is already recorded (e.g. a Play Again live match
    // later consolidated from its finished .replay, or a rescan after an upgrade).
    // Returns true if it is a brand new match.
    public bool AddMatch(MatchChestCounts m, string source = "replay", string? replayFile = null)
    {
        if (string.IsNullOrEmpty(m.MatchId)) return false;
        _ids ??= new HashSet<string>(Records.Select(r => r.MatchId), StringComparer.OrdinalIgnoreCase);

        MatchRecord? existing = null;
        if (_ids.Contains(m.MatchId))
        {
            existing = Records.FirstOrDefault(r => string.Equals(r.MatchId, m.MatchId, StringComparison.OrdinalIgnoreCase));
        }
        bool FileAlias(MatchRecord r) => replayFile != null
            && (string.Equals(r.MatchId, "play-again:" + replayFile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.MatchId, "ocr-rescue:" + replayFile, StringComparison.OrdinalIgnoreCase));
        if (existing == null && !string.IsNullOrEmpty(replayFile))
        {
            // A rotated filename alone cannot join two different real session IDs.
            bool incomingAlias = m.MatchId.Equals("play-again:" + replayFile, StringComparison.OrdinalIgnoreCase)
                || m.MatchId.Equals("ocr-rescue:" + replayFile, StringComparison.OrdinalIgnoreCase);
            existing = Records.FirstOrDefault(r => FileAlias(r)
                || (incomingAlias && string.Equals(r.ReplayFile, replayFile, StringComparison.OrdinalIgnoreCase)));
        }

        if (existing != null)
        {
            // Never overwrite an authoritative bit-parsed replay with a live/pending estimate
            if (existing.Source == "replay" && source != "replay")
            {
                if (!string.IsNullOrEmpty(replayFile) && string.IsNullOrEmpty(existing.ReplayFile))
                    existing.ReplayFile = replayFile;
                return false;
            }

            if (existing.MatchId != m.MatchId)
            {
                _ids.Remove(existing.MatchId);
                existing.MatchId = m.MatchId;
                _ids.Add(m.MatchId);
            }

            if (source == "replay")
            {
                foreach (var duplicate in Records.Where(r => r != existing && r.Source != "replay" && FileAlias(r)).ToList())
                {
                    Records.Remove(duplicate);
                    _ids.Remove(duplicate.MatchId);
                }
            }
            DateTime oldTime = existing.Time;
            if (m.Time != DateTime.MinValue && source == "replay")
                existing.Time = m.Time;

            existing.You = m.You;
            existing.YourChestKinds = m.YourChestKinds;
            existing.Unattributed = m.Unattributed;
            existing.Mates = m.Mates;
            existing.Prizes = m.Prizes;
            if (!string.IsNullOrEmpty(m.Playlist)) existing.Playlist = m.Playlist;
            if (!string.IsNullOrEmpty(replayFile)) existing.ReplayFile = replayFile;
            existing.Source = source;

            if (existing.Time != oldTime && Records.Count >= 2)
                Records.Sort(static (a, b) => a.Time.CompareTo(b.Time));

            return false;
        }

        _ids.Add(m.MatchId);
        Records.Add(new MatchRecord
        {
            MatchId = m.MatchId,
            Time = m.Time,
            You = m.You,
            YourChestKinds = m.YourChestKinds,
            Playlist = m.Playlist,
            Source = source,
            ReplayFile = string.IsNullOrEmpty(replayFile) ? null : replayFile,
            Unattributed = m.Unattributed,
            Mates = m.Mates,
            Prizes = m.Prizes,
        });
        if (Records.Count >= 2 && Records[^1].Time < Records[^2].Time)
            Records.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return true;
    }

    private static bool MatchesSession(MatchRecord record, string fileName, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            string.Equals(record.MatchId, sessionId, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(fileName)) return false;
        return string.Equals(record.MatchId, "play-again:" + fileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.MatchId, "ocr-rescue:" + fileName, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(sessionId) &&
                string.Equals(record.ReplayFile, fileName, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasReplayRecord(string replayFileName, string? sessionId = null) =>
        Records.Any(r => MatchesSession(r, replayFileName, sessionId));

    public bool RemovePendingPlayAgain(string replayFileName, string? sessionId = null) =>
        RemoveEstimates(replayFileName, sessionId, includeRescued: false);

    public bool RemoveEstimates(string fileName, string? sessionId, bool includeRescued = true) =>
        Records.RemoveAll(r =>
        {
            bool provisional = r.Source == "play-again" || (includeRescued && r.Source == "ocr-rescue");
            if (!provisional || !MatchesSession(r, fileName, sessionId)) return false;
            _ids?.Remove(r.MatchId);
            return true;
        }) > 0;

    // Who you play with most, by matches together.
    public List<MateTotal> TopMates(DateTime? since, int take) =>
        Records.Where(r => since is not { } s || r.Time >= s)
            .SelectMany(r => r.Mates.Where(m => !m.Unnamed).Select(m => (Match: r, Mate: m)))
            .GroupBy(x => x.Mate.AccountKey is { } key ? "account:" + key : "nome:" + x.Mate.Name)
            .Select(g => new MateTotal(g.Last().Mate.Name, g.Select(x => x.Match).Distinct().Count(),
                g.Sum(x => (long)x.Mate.Chests)))
            .OrderByDescending(t => t.Matches).ThenByDescending(t => t.Chests)
            .Take(take).ToList();

    // Matches on/after `since` (null = all time), oldest-first by time.
    public List<MatchRecord> RecordsSince(DateTime? since) =>
        Records.Where(r => since is not { } s || r.Time >= s).OrderBy(r => r.Time).ToList();

    // Aggregate over matches on/after `since` (null = all time).
    public Period Over(DateTime? since)
    {
        int n = 0;
        long y = 0;
        long sq = 0;
        foreach (var r in Records)
        {
            if (since is { } s && r.Time < s) continue;
            n++;
            y += r.You;
            sq += r.SquadVisibleChests;
        }
        return new Period(n, y, sq, n > 0 ? (double)y / n : 0);
    }

    // Builds chronological cumulative series for progression & probability charts.
    public List<CumulativePoint> CumulativeSeries(DateTime? since)
    {
        var ordered = RecordsSince(since);
        var list = new List<CumulativePoint>(ordered.Count);
        long cumYou = 0;
        long cumSquad = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            cumYou += r.You;
            cumSquad += r.SquadVisibleChests;
            list.Add(new CumulativePoint(i + 1, r.Time, r.You, cumYou, cumSquad));
        }
        return list;
    }

    public static string FriendlyPlaylist(string? playlist, string? source = null)
    {
        if (source == "ocr-rescue") return "Resgatada via OCR";
        if (string.IsNullOrWhiteSpace(playlist)) return "Battle Royale";
        return playlist.Trim().ToLowerInvariant() switch
        {
            "playlist_nobuildbr_squad" => "Zero Build · Esquadrão",
            "playlist_nobuildbr_trio" => "Zero Build · Trio",
            "playlist_nobuildbr_duo" => "Zero Build · Dupla",
            "playlist_nobuildbr_solo" => "Zero Build · Solo",
            "playlist_defaultsquad" => "Battle Royale · Esquadrão",
            "playlist_defaulttrio" => "Battle Royale · Trio",
            "playlist_defaultduo" => "Battle Royale · Dupla",
            "playlist_defaultsolo" => "Battle Royale · Solo",
            _ => playlist,
        };
    }
}

public static class StatsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        AppChannel.DataDirectory, "stats.json");

    public static LifetimeStats Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                var stats = JsonSerializer.Deserialize<LifetimeStats>(fs) ?? new();
                if (stats.Version < LifetimeStats.CurrentVersion)
                {
                    // Let the next parallel scan enrich replays still on disk (e.g. Playlist names)
                    // while preserving every historical match whose replay was already rotated out.
                    stats.ScannedFiles.Clear();
                    stats.AbandonedFiles.Clear();
                    stats.Version = LifetimeStats.CurrentVersion;
                }
                if (stats.Records.Count > 1)
                    stats.Records.Sort(static (a, b) => a.Time.CompareTo(b.Time));
                return stats;
            }
        }
        catch { /* corrupt/old file -> start fresh rather than crash */ }
        return new() { Version = LifetimeStats.CurrentVersion };
    }

    public static void Save(string path, LifetimeStats stats) =>
        WriteAtomically(path, JsonSerializer.Serialize(stats, Options));

    internal static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, contents);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); } catch { }
        }
    }
}
