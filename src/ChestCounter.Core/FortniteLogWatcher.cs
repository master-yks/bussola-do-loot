using System.Globalization;

namespace ChestCounter;

public readonly record struct FortniteMatchSnapshot(
    string? SessionId,
    string? PlaylistId,
    DateTime MatchStartedUtc,
    DateTime EventUtc);

// Lightweight, non-locking tail reader for %LOCALAPPDATA%\FortniteGame\Saved\Logs\FortniteGame.log.
// Detects instant in-game "Play Again" ("Jogar novamente") triggers, match session IDs, playlists,
// lobby returns, and demo stop events without ever blocking or locking Fortnite's log file.
public sealed class FortniteLogWatcher
{
    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FortniteGame", "Saved", "Logs", "FortniteGame.log");

    private readonly string _logPath;
    private long _lastOffset;
    private DateTime _lastCreationUtc = DateTime.MinValue;

    private string? _pendingBrowseSessionId;

    public string? CurrentSessionId { get; private set; }
    public string? CurrentPlaylistId { get; private set; }
    public DateTime CurrentMatchStartUtc { get; private set; } = DateTime.MinValue;
    public DateTime LastDemoStoppedUtc { get; private set; } = DateTime.MinValue;
    public bool IsInMatch { get; private set; }
    public bool IsPlayAgainActive { get; private set; }

    public event Action<FortniteMatchSnapshot>? MatchStarted;
    public event Action<FortniteMatchSnapshot>? DemoRecordingStarted;
    public event Action<FortniteMatchSnapshot>? PlayAgainTriggered;
    public event Action? ReturnedToLobby;
    public event Action<DateTime>? DemoStopped;

    public FortniteLogWatcher(string? logPath = null)
    {
        _logPath = logPath ?? DefaultLogPath;
    }

    // Reconstructs current match/session state from an already-running Fortnite session without firing past events.
    public void CatchUpExistingLog(bool isFortniteRunning)
    {
        ResetState();
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists) return;

            _lastCreationUtc = fi.CreationTimeUtc;
            if (!isFortniteRunning)
            {
                _lastOffset = fi.Length;
                return;
            }

            using var fs = OpenSharedRead(_logPath);
            long len = fs.Length;
            // Reading at most the last 32 MB is more than enough to cover hours of gameplay and takes ~15ms.
            long startPos = Math.Max(0, len - 32L * 1024 * 1024);
            fs.Seek(startPos, SeekOrigin.Begin);

            long consumed = ReadCompleteLines(fs, startPos, skipFirstPartialLine: startPos > 0, fireEvents: false);
            _lastOffset = consumed;
        }
        catch
        {
            // Log file may not exist yet or be mid-rotation; Poll() will pick it up cleanly.
        }
    }

    // Reads any newly appended complete lines since the last poll and raises lifecycle events.
    public void Poll()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists) return;

            // Detect log truncation / rotation when Fortnite restarts
            if (fi.Length < _lastOffset || (_lastCreationUtc != DateTime.MinValue && fi.CreationTimeUtc != _lastCreationUtc))
            {
                ResetState();
                _lastOffset = 0;
            }
            _lastCreationUtc = fi.CreationTimeUtc;

            if (fi.Length == _lastOffset) return;

            using var fs = OpenSharedRead(_logPath);
            if (fs.Length < _lastOffset)
            {
                ResetState();
                _lastOffset = 0;
            }
            if (fs.Length == _lastOffset) return;

            fs.Seek(_lastOffset, SeekOrigin.Begin);
            _lastOffset = ReadCompleteLines(fs, _lastOffset, skipFirstPartialLine: false, fireEvents: true);
        }
        catch
        {
            // Ignore transient sharing/rotation races; next 200ms poll retries from _lastOffset.
        }
    }

    // Feed a single log line directly (also used by deterministic unit/integration verification).
    public void ProcessLine(string line, bool fireEvents)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Fast pre-check: skip 99.9% of engine log lines before doing any substring extraction
        if (!line.Contains("LogIGMWidgets", StringComparison.Ordinal)
            && !line.Contains("LogDemo", StringComparison.Ordinal)
            && !line.Contains("Metadata set :", StringComparison.Ordinal)
            && !line.Contains("OnRep_CurrentPlaylistInfo", StringComparison.Ordinal)
            && !line.Contains("BarrierId=[ReadyUp]", StringComparison.Ordinal)
            && !line.Contains("Starting matchmaking attempt", StringComparison.Ordinal)
            && !line.Contains("ReturnToMainMenu", StringComparison.Ordinal)
            && !line.Contains("EncryptionToken=", StringComparison.Ordinal))
        {
            return;
        }

        DateTime lineUtc = TryParseUtcTimestamp(line, out var parsedUtc) ? parsedUtc : DateTime.UtcNow;

        // 1) Pre-travel server browse carries EncryptionToken=<AccountId>:<SessionId> right before StopDemo of old match
        if (line.Contains("EncryptionToken=", StringComparison.Ordinal)
            && (line.Contains("LoadMap:", StringComparison.Ordinal) || line.Contains("Browse:", StringComparison.Ordinal)))
        {
            var tokenSession = ExtractAfterColonBeforeDelimiter(line, "EncryptionToken=");
            if (!string.IsNullOrEmpty(tokenSession))
                _pendingBrowseSessionId = tokenSession;
            return;
        }

        // 2) Playlist replication / profiler metadata
        if (line.Contains("OnRep_CurrentPlaylistInfo() PlaylistName is ", StringComparison.Ordinal))
        {
            var pl = ExtractTokenAfter(line, "OnRep_CurrentPlaylistInfo() PlaylistName is ");
            ApplyPlaylist(pl);
            return;
        }

        if (line.Contains("Metadata set : playlistid=\"", StringComparison.Ordinal))
        {
            var pl = ExtractQuotedAfter(line, "Metadata set : playlistid=\"");
            ApplyPlaylist(pl);
            return;
        }

        // 3) Match session ID set when entering InProgress in the new match (always after previous StopDemo)
        if (line.Contains("Metadata set : sessionid=\"", StringComparison.Ordinal))
        {
            var sid = ExtractQuotedAfter(line, "Metadata set : sessionid=\"");
            if (!string.IsNullOrEmpty(sid))
            {
                if (IsInMatch && string.Equals(CurrentSessionId, sid, StringComparison.OrdinalIgnoreCase))
                    return;
                CurrentSessionId = sid;
                _pendingBrowseSessionId = null;
                CurrentMatchStartUtc = lineUtc;
                IsInMatch = true;
                IsPlayAgainActive = false;
                if (fireEvents)
                    MatchStarted?.Invoke(new FortniteMatchSnapshot(CurrentSessionId, CurrentPlaylistId, CurrentMatchStartUtc, lineUtc));
            }
            return;
        }

        // 4) Replay driver starts recording the match
        if (line.Contains("LogDemo: UReplaySubsystem::RecordReplay: Starting recording", StringComparison.Ordinal))
        {
            if (IsPlayAgainActive && _pendingBrowseSessionId == null) return;
            if (string.IsNullOrEmpty(CurrentSessionId) && !string.IsNullOrEmpty(_pendingBrowseSessionId))
            {
                CurrentSessionId = _pendingBrowseSessionId;
                _pendingBrowseSessionId = null;
            }
            if (CurrentMatchStartUtc == DateTime.MinValue || !IsInMatch)
                CurrentMatchStartUtc = lineUtc;

            IsInMatch = true;
            IsPlayAgainActive = false;
            if (fireEvents)
                DemoRecordingStarted?.Invoke(new FortniteMatchSnapshot(CurrentSessionId, CurrentPlaylistId, CurrentMatchStartUtc, lineUtc));
            return;
        }

        // 5) In-game "Play Again" ("Jogar novamente") triggered at the end of a match!
        if (IsInMatch && !IsFrontendPlaylist(CurrentPlaylistId))
        {
            bool isPlayAgain =
                line.Contains("UAthenaReadyUpScreenOverlay::Activate()", StringComparison.Ordinal)
                || (line.Contains("barrier_locally_signaled", StringComparison.Ordinal)
                    && line.Contains("BarrierId=[ReadyUp]", StringComparison.Ordinal))
                || line.Contains("Starting matchmaking attempt ", StringComparison.Ordinal);

            if (isPlayAgain)
            {
                if (!IsPlayAgainActive)
                {
                    IsPlayAgainActive = true;
                    if (fireEvents)
                    {
                        PlayAgainTriggered?.Invoke(new FortniteMatchSnapshot(
                            CurrentSessionId,
                            CurrentPlaylistId,
                            CurrentMatchStartUtc != DateTime.MinValue ? CurrentMatchStartUtc : lineUtc,
                            lineUtc));
                    }
                }
                return;
            }
        }

        // 6) Player explicitly returns to the Lobby
        if (line.Contains("FortPC::ReturnToMainMenu()", StringComparison.Ordinal))
        {
            IsInMatch = false;
            IsPlayAgainActive = false;
            CurrentSessionId = null;
            if (fireEvents)
                ReturnedToLobby?.Invoke();
            return;
        }

        // 7) Fortnite stops recording and flushes the .replay file (both on Lobby return and on Play Again server travel)
        if (line.Contains("LogDemo: StopDemo:", StringComparison.Ordinal))
        {
            LastDemoStoppedUtc = lineUtc;
            IsInMatch = false;
            IsPlayAgainActive = false;
            CurrentSessionId = null;
            if (fireEvents)
                DemoStopped?.Invoke(lineUtc);
        }
    }

    private void ApplyPlaylist(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist) || playlist.Equals("None", StringComparison.OrdinalIgnoreCase))
            return;

        if (IsFrontendPlaylist(playlist))
        {
            CurrentPlaylistId = playlist;
            CurrentSessionId = null;
            IsInMatch = false;
            IsPlayAgainActive = false;
        }
        else
        {
            CurrentPlaylistId = playlist;
        }
    }

    public static bool IsFrontendPlaylist(string? playlist) =>
        playlist != null && playlist.Equals("Playlist_FrontEnd", StringComparison.OrdinalIgnoreCase);

    private void ResetState()
    {
        CurrentSessionId = null;
        CurrentPlaylistId = null;
        CurrentMatchStartUtc = DateTime.MinValue;
        LastDemoStoppedUtc = DateTime.MinValue;
        _pendingBrowseSessionId = null;
        IsInMatch = false;
        IsPlayAgainActive = false;
    }

    private static FileStream OpenSharedRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 32768,
        FileOptions.SequentialScan);

    private long ReadCompleteLines(FileStream fs, long baseOffset, bool skipFirstPartialLine, bool fireEvents)
    {
        long remaining = fs.Length - baseOffset;
        if (remaining <= 0) return baseOffset;

        long consumed = baseOffset;
        long chunkOffset = baseOffset;
        byte[] buffer = new byte[Math.Min(remaining, 1024 * 1024)];
        using var carry = new MemoryStream();
        bool skippingFirst = skipFirstPartialLine;

        while (true)
        {
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            int lineStart = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;

                int segLen = i - lineStart;
                if (skippingFirst)
                {
                    skippingFirst = false;
                    carry.SetLength(0);
                }
                else if (carry.Length == 0)
                {
                    int trimLen = segLen > 0 && buffer[lineStart + segLen - 1] == (byte)'\r' ? segLen - 1 : segLen;
                    if (trimLen > 0)
                    {
                        string line = System.Text.Encoding.UTF8.GetString(buffer, lineStart, trimLen);
                        ProcessLine(line, fireEvents);
                    }
                }
                else
                {
                    if (segLen > 0) carry.Write(buffer, lineStart, segLen);
                    byte[] full = carry.GetBuffer();
                    int fullLen = (int)carry.Length;
                    if (fullLen > 0 && full[fullLen - 1] == (byte)'\r') fullLen--;
                    if (fullLen > 0)
                    {
                        string line = System.Text.Encoding.UTF8.GetString(full, 0, fullLen);
                        ProcessLine(line, fireEvents);
                    }
                    carry.SetLength(0);
                }

                consumed = chunkOffset + i + 1;
                lineStart = i + 1;
            }

            if (lineStart < read && !skippingFirst)
            {
                carry.Write(buffer, lineStart, read - lineStart);
            }

            chunkOffset += read;
        }

        return consumed;
    }

    // Parses "[2026.11.25-03.00.56:057]" at the start of Unreal Engine log lines (UTC).
    public static bool TryParseUtcTimestamp(ReadOnlySpan<char> line, out DateTime utc)
    {
        utc = default;
        if (line.Length < 25
            || line[0] != '['
            || line[5] != '.'
            || line[8] != '.'
            || line[11] != '-'
            || line[14] != '.'
            || line[17] != '.'
            || line[20] != ':'
            || line[24] != ']')
        {
            return false;
        }

        if (!int.TryParse(line.Slice(1, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            || !int.TryParse(line.Slice(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(line.Slice(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(line.Slice(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
            || !int.TryParse(line.Slice(15, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minute)
            || !int.TryParse(line.Slice(18, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int second)
            || !int.TryParse(line.Slice(21, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int ms))
        {
            return false;
        }

        try
        {
            utc = new DateTime(year, month, day, hour, minute, second, ms, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractQuotedAfter(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end].Trim() : null;
    }

    private static string? ExtractTokenAfter(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        while (start < line.Length && line[start] == ' ') start++;
        int end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ')' && line[end] != ']')
            end++;
        return end > start ? line[start..end] : null;
    }

    private static string? ExtractAfterColonBeforeDelimiter(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        int colon = line.IndexOf(':', start);
        if (colon < 0) return null;
        int sStart = colon + 1;
        int sEnd = sStart;
        while (sEnd < line.Length && char.IsAsciiHexDigit(line[sEnd]))
            sEnd++;
        return sEnd - sStart >= 16 ? line[sStart..sEnd] : null;
    }
}
