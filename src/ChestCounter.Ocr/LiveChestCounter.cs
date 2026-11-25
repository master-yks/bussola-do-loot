using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using ChestCounter;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestCounter.Ocr;

// Real-time chest counter + automatic match & "Play Again" ("Jogar novamente") watcher:
// 1) Screen-captures the Fortnite XP popup region and runs multi-scale Windows.Media.Ocr so every chest
//    is counted live during the match (and persisted to LiveSessionJournal against mid-game crashes).
// 2) Tails FortniteGame.log non-lockingly so clicking "Jogar novamente" immediately finalizes & counts
//    the match and resets the live counter without requiring a return to the Lobby.
// 3) Watches %LOCALAPPDATA%\FortniteGame\Saved\Demos via FileSystemWatcher + verification loop so every
//    completed .replay is parsed with bit-level accuracy and seamlessly consolidated into LifetimeStats.
public sealed partial class LiveChestCounter
{
    private readonly OcrEngine? _engine = CreateEngine();

    private static OcrEngine? CreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language("pt-BR"))
                ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public string? LanguageTag => _engine?.RecognizerLanguage.LanguageTag;

    private readonly object _stateLock = new();
    private readonly FortniteLogWatcher _logWatcher;
    private readonly HashSet<string> _closingReplays = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _finalizedSessionIds = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _watchWake = new(0, 1);

    // False if Windows has no OCR language installed (replay & log watching still work even without OCR!).
    public bool Available => _engine != null;

    // Raised when this match's live searched-chest count changes (null = between matches).
    public event Action<int?>? LiveChanged;

    // Raised whenever one or more finished matches are automatically folded into LifetimeStats.
    public event Action<RunReport, LifetimeStats>? StatsUpdated;

    // Raised with human-readable log lines (match started, chest detected, match finalized...).
    public event Action<string>? Log;

    // Raised with the text of every XP popup the OCR reads, chest or not.
    public event Action<string>? PopupRead;

    private volatile int _liveRaw = -1; // -1 = null (between matches)
    private volatile string? _activeReplayName;
    private string? _activeSessionId;
    private string? _activePlaylist;
    private DateTime _activeStartUtc = DateTime.MinValue;
    private DateTime _lastLiveActivityUtc = DateTime.MinValue;
    private DateTime _lastDemoStopUtc = DateTime.MinValue;
    private volatile bool _inPlayAgainQueue;

    public int? CurrentLiveCount => _liveRaw < 0 ? null : _liveRaw;
    public bool IsInPlayAgainQueue => _inPlayAgainQueue;

    public LiveChestCounter(string? customLogPath = null)
    {
        _logWatcher = new FortniteLogWatcher(customLogPath);
        _logWatcher.MatchStarted += OnLogMatchStarted;
        _logWatcher.DemoRecordingStarted += OnLogDemoRecordingStarted;
        _logWatcher.PlayAgainTriggered += HandlePlayAgainTriggered;
        _logWatcher.ReturnedToLobby += OnLogReturnedToLobby;
        _logWatcher.DemoStopped += OnLogDemoStopped;
    }

    // Called by the GUI after a manual/startup scan finishes so if the active replay was just indexed,
    // the live match counter resets immediately and never double-counts those chests.
    public void NotifyExternalScan(LifetimeStats updatedStats)
    {
        bool shouldNotifyNull = false;
        lock (_stateLock)
        {
            var active = _activeReplayName;
            if (string.IsNullOrEmpty(active)) return;
            try
            {
                var activeFull = Path.Combine(ChestCounterService.DefaultDemosPath, active);
                if (!File.Exists(activeFull)) return;
                var aKey = ReplayScanner.FileKey(activeFull);
                if (updatedStats.ScannedFiles.Contains(aKey) || updatedStats.AbandonedFiles.Contains(aKey))
                {
                    _closingReplays.Remove(active);
                    _activeReplayName = null;
                    _activeSessionId = null;
                    if (_liveRaw >= 0)
                    {
                        _liveRaw = -1;
                        shouldNotifyNull = true;
                    }
                }
            }
            catch { }
        }

        if (shouldNotifyNull)
            LiveChanged?.Invoke(null);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetupFileSystemWatcher();
        var initTask = Task.Run(InitializeLogState, token);
        _ = Task.Run(() => LogWatchLoop(initTask, token));
        _ = Task.Run(() => MatchWatchLoop(initTask, token));
        if (_engine != null)
            _ = Task.Run(() => CaptureLoop(token));
    }

    public void Stop()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        _cts?.Cancel();
        _cts = null;
        lock (_stateLock)
        {
            _inPlayAgainQueue = false;
            _liveRaw = -1;
        }
        LiveChanged?.Invoke(null);
    }

    private void WakeWatcher()
    {
        if (_watchWake.CurrentCount == 0)
        {
            try { _watchWake.Release(); } catch { }
        }
    }

    private void SetupFileSystemWatcher()
    {
        try
        {
            var demos = ChestCounterService.DefaultDemosPath;
            if (!Directory.Exists(demos)) return;
            var fsw = new FileSystemWatcher(demos, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            void Wake(object _, FileSystemEventArgs __) => WakeWatcher();
            fsw.Created += Wake;
            fsw.Changed += Wake;
            fsw.Renamed += (_, e) => Wake(_, e);
            _watcher = fsw;
        }
        catch { _watcher = null; }
    }

    private void InitializeLogState()
    {
        _logWatcher.CatchUpExistingLog(IsFortniteRunning());
        lock (_stateLock)
        {
            _activeSessionId = _logWatcher.CurrentSessionId;
            _activePlaylist = _logWatcher.CurrentPlaylistId;
            if (_logWatcher.CurrentMatchStartUtc != DateTime.MinValue)
                _activeStartUtc = _logWatcher.CurrentMatchStartUtc;
            if (_logWatcher.LastDemoStoppedUtc != DateTime.MinValue)
                _lastDemoStopUtc = _logWatcher.LastDemoStoppedUtc;
            _inPlayAgainQueue = _logWatcher.IsPlayAgainActive;
        }
    }

    private async Task LogWatchLoop(Task initTask, CancellationToken token)
    {
        try { await initTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { }

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logWatcher.Poll();
            }
            catch { }

            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnLogMatchStarted(FortniteMatchSnapshot snap)
    {
        bool emitZero = false;
        lock (_stateLock)
        {
            _inPlayAgainQueue = false;
            if (!string.IsNullOrEmpty(snap.SessionId))
                _activeSessionId = snap.SessionId;
            if (!string.IsNullOrEmpty(snap.PlaylistId))
                _activePlaylist = snap.PlaylistId;
            if (snap.MatchStartedUtc != DateTime.MinValue)
                _activeStartUtc = snap.MatchStartedUtc;
            _lastLiveActivityUtc = DateTime.UtcNow;

            if (_liveRaw < 0 && !FortniteLogWatcher.IsFrontendPlaylist(_activePlaylist))
            {
                _liveRaw = 0;
                emitZero = true;
            }
        }

        if (emitZero)
            LiveChanged?.Invoke(0);
        WakeWatcher();
    }

    private void OnLogDemoRecordingStarted(FortniteMatchSnapshot snap)
    {
        bool emitZero = false;
        lock (_stateLock)
        {
            _inPlayAgainQueue = false;
            if (!string.IsNullOrEmpty(snap.SessionId))
                _activeSessionId = snap.SessionId;
            if (!string.IsNullOrEmpty(snap.PlaylistId))
                _activePlaylist = snap.PlaylistId;
            if (snap.MatchStartedUtc != DateTime.MinValue)
                _activeStartUtc = snap.MatchStartedUtc;
            _lastLiveActivityUtc = DateTime.UtcNow;

            if (_liveRaw < 0 && !FortniteLogWatcher.IsFrontendPlaylist(_activePlaylist))
            {
                _liveRaw = 0;
                emitZero = true;
            }
        }

        if (emitZero)
            LiveChanged?.Invoke(0);
        WakeWatcher();
    }

    private void OnLogReturnedToLobby()
    {
        lock (_stateLock)
        {
            _inPlayAgainQueue = false;
        }
        WakeWatcher();
    }

    private void OnLogDemoStopped(DateTime stoppedUtc)
    {
        lock (_stateLock)
        {
            _lastDemoStopUtc = DateTime.UtcNow;
        }
        WakeWatcher();
        // Schedule a quick follow-up wake right after the 0.65s flush window so replay consolidation is sub-second!
        var cts = _cts;
        if (cts != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750, cts.Token).ConfigureAwait(false);
                    WakeWatcher();
                }
                catch { }
            });
        }
    }

    // Triggered within ~150ms of the player clicking "Play Again" ("Jogar novamente") in Fortnite!
    private void HandlePlayAgainTriggered(FortniteMatchSnapshot snap)
    {
        string? replayName;
        string? sessionId;
        string? playlist;
        DateTime startUtc;
        int chests;

        // If _activeReplayName wasn't bound yet (e.g. tracker launched right on the post-match screen),
        // locate the most recent unscanned replay from the last few minutes before locking.
        string? fallbackReplayName = _activeReplayName == null ? FindRecentUnscannedReplayName() : null;

        lock (_stateLock)
        {
            replayName = _activeReplayName ?? fallbackReplayName;
            sessionId = !string.IsNullOrEmpty(snap.SessionId) ? snap.SessionId : _activeSessionId;
            playlist = !string.IsNullOrEmpty(snap.PlaylistId) ? snap.PlaylistId : _activePlaylist;
            startUtc = _activeStartUtc != DateTime.MinValue ? _activeStartUtc : snap.MatchStartedUtc;
            chests = Math.Max(0, _liveRaw);

            if (!string.IsNullOrEmpty(replayName) && _closingReplays.Contains(replayName))
                return;
            if (!string.IsNullOrEmpty(sessionId) && _finalizedSessionIds.Contains(sessionId))
                return;

            // Nothing active to finalize
            if (string.IsNullOrEmpty(replayName) && string.IsNullOrEmpty(sessionId) && _liveRaw < 0)
            {
                _inPlayAgainQueue = true;
                return;
            }

            _inPlayAgainQueue = true;
            if (!string.IsNullOrEmpty(replayName))
                _closingReplays.Add(replayName);
            if (!string.IsNullOrEmpty(sessionId))
            {
                if (_finalizedSessionIds.Count >= 256) _finalizedSessionIds.Clear();
                _finalizedSessionIds.Add(sessionId);
            }

            _activeReplayName = null;
            _activeSessionId = null;
            _liveRaw = -1;
        }

        try
        {
            var (run, updatedStats) = ChestCounterService.FinalizePlayAgainMatch(
                sessionId, playlist, startUtc, chests, replayName);

            LiveChanged?.Invoke(null);
            StatsUpdated?.Invoke(run, updatedStats);

            var season = updatedStats.Over(ChestCounterService.SeasonStartUtc);
            if (run.NewMatches > 0 || run.AlreadyCounted > 0)
            {
                Log?.Invoke(
                    $"[{DateTime.Now:HH:mm:ss}] Jogar novamente detectado! Partida finalizada ({chests} baús) e contador resetado. " +
                    $"Temporada: {season.YourChests:N0} baús ({season.Matches} partidas).");
            }
            else if (run.SkippedMode > 0)
            {
                Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Jogar novamente detectado (modo fora do evento) — contador resetado.");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Aviso ao finalizar partida no Jogar novamente: {ex.Message}");
            LiveChanged?.Invoke(null);
        }
    }

    private static string? FindRecentUnscannedReplayName()
    {
        try
        {
            var files = ChestCounterService.FindReplays();
            if (files.Count == 0) return null;
            var stats = ChestCounterService.LoadStats();
            var newest = files[0];
            var fi = new FileInfo(newest);
            if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes > 10) return null;
            var key = ReplayScanner.FileKey(newest);
            if (stats.ScannedFiles.Contains(key) || stats.AbandonedFiles.Contains(key)) return null;
            return fi.Name;
        }
        catch
        {
            return null;
        }
    }

    // Continuously watches the Demos folder so EVERY match in the season is automatically detected
    // when it starts and meticulously parsed & summed the moment it ends.
    private async Task MatchWatchLoop(Task initTask, CancellationToken token)
    {
        try { await initTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch { }

        var observedLengths = new Dictionary<string, (long Length, DateTime StableSinceUtc)>(StringComparer.OrdinalIgnoreCase);

        var failedFiles = new Dictionary<string, (string Key, DateTime RetryAfter)>(StringComparer.OrdinalIgnoreCase);
        (DateTime Modified, long Size)? indexVersion = null;
        var statsSnapshot = new LifetimeStats();

        // Restore any in-progress live session if the app was restarted mid-match!
        var savedLive = LiveSessionJournal.Load();
        if (savedLive != null && !string.IsNullOrEmpty(savedLive.ReplayFileName))
        {
            var candidatePath = Path.Combine(ChestCounterService.DefaultDemosPath, savedLive.ReplayFileName);
            if (File.Exists(candidatePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidatePath)).TotalMinutes <= 45)
            {
                var initStats = ChestCounterService.LoadStats();
                var initKey = ReplayScanner.FileKey(candidatePath);
                bool alreadyIndexed = initStats.ScannedFiles.Contains(initKey) || initStats.AbandonedFiles.Contains(initKey);

                if (!alreadyIndexed)
                {
                    if (savedLive.FinalizedViaPlayAgain)
                    {
                        lock (_stateLock)
                        {
                            _closingReplays.Add(savedLive.ReplayFileName);
                            if (!string.IsNullOrEmpty(savedLive.SessionId))
                                _finalizedSessionIds.Add(savedLive.SessionId);
                        }
                    }
                    else if (_inPlayAgainQueue)
                    {
                        // User pressed "Play Again" right before opening the tracker: finalize immediately!
                        lock (_stateLock)
                        {
                            _activeReplayName = savedLive.ReplayFileName;
                            _activeStartUtc = savedLive.StartedUtc;
                            _activeSessionId ??= savedLive.SessionId;
                            _activePlaylist ??= savedLive.Playlist;
                            _liveRaw = savedLive.LiveChests;
                        }
                        HandlePlayAgainTriggered(new FortniteMatchSnapshot(
                            _activeSessionId, _activePlaylist, savedLive.StartedUtc, DateTime.UtcNow));
                    }
                    else
                    {
                        int restoredLive;
                        lock (_stateLock)
                        {
                            _activeReplayName = savedLive.ReplayFileName;
                            _activeStartUtc = savedLive.StartedUtc;
                            _activeSessionId ??= savedLive.SessionId;
                            _activePlaylist ??= savedLive.Playlist;
                            _liveRaw = savedLive.LiveChests;
                            restoredLive = _liveRaw;
                        }
                        LiveChanged?.Invoke(restoredLive);
                    }
                }
            }
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                var files = ChestCounterService.FindReplays();
                if (files.Count > 0)
                {
                    var history = new FileInfo(ChestCounterService.StatsPath);
                    var version = history.Exists ? (history.LastWriteTimeUtc, history.Length) : (DateTime.MinValue, 0L);
                    if (indexVersion != version)
                    {
                        statsSnapshot = ChestCounterService.LoadStats();
                        indexVersion = version;
                    }
                }
                var nowUtc = DateTime.UtcNow;
                var readyToScan = new List<string>();
                FileInfo? newestGrowingCandidate = null;

                double requiredStableSeconds;
                lock (_stateLock)
                {
                    requiredStableSeconds = _lastDemoStopUtc != DateTime.MinValue
                        && (nowUtc - _lastDemoStopUtc).TotalSeconds <= 20
                        ? 0.65
                        : 2.5;
                }

                // Oldest-first inspection
                foreach (var file in files.AsEnumerable().Reverse())
                {
                    string fileName = Path.GetFileName(file);
                    string key = ReplayScanner.FileKey(file);
                    if (statsSnapshot.ScannedFiles.Contains(key) || statsSnapshot.AbandonedFiles.Contains(key))
                    {
                        failedFiles.Remove(file);
                        observedLengths.Remove(file);
                        lock (_stateLock) { _closingReplays.Remove(fileName); }
                        continue;
                    }

                    if (failedFiles.TryGetValue(file, out var failure) && failure.Key == key && nowUtc < failure.RetryAfter)
                        continue;

                    FileInfo fi;
                    try { fi = new FileInfo(file); }
                    catch { continue; }

                    long len = fi.Length;

                    bool observed = observedLengths.TryGetValue(file, out var prev);
                    if (!observed || prev.Length != len)
                    {
                        observedLengths[file] = (len, nowUtc);

                        bool isClosingSpectatorReplay;
                        lock (_stateLock)
                        {
                            isClosingSpectatorReplay = _closingReplays.Contains(fileName);
                        }

                        // Ignore spectator growth on a replay already finalized via "Play Again"!
                        if (observed && len > prev.Length && !isClosingSpectatorReplay
                            && (nowUtc - fi.LastWriteTimeUtc).TotalMinutes <= 40)
                        {
                            newestGrowingCandidate = fi;
                        }
                        continue;
                    }

                    // Wait for stable file size so Fortnite finishes flushing the AES key to the header.
                    if ((nowUtc - prev.StableSinceUtc).TotalSeconds < requiredStableSeconds)
                        continue;

                    readyToScan.Add(file);
                }

                // 1) Scan finished replays FIRST so any completed match is folded into LifetimeStats
                //    before switching _activeReplayName / resetting _liveRaw to 0 for a newly started match.
                if (readyToScan.Count > 0)
                {
                    var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    var scanKeys = readyToScan.ToDictionary(file => Path.GetFileName(file), ReplayScanner.FileKey);
                    var (run, updatedStats) = ChestCounterService.Scan(readyToScan, (_, _, name, error) =>
                    {
                        if (error == null) return;
                        failures.Enqueue(name);
                        Log?.Invoke($"Falha ao ler {name}: {error}");
                    });
                    foreach (var name in failures)
                        failedFiles[Path.Combine(ChestCounterService.DefaultDemosPath, name)] = (scanKeys[name], nowUtc.AddMinutes(5));

                    // Clean up any closing replays that have now been indexed
                    lock (_stateLock)
                    {
                        foreach (var scannedPath in readyToScan)
                        {
                            var sKey = ReplayScanner.FileKey(scannedPath);
                            if (updatedStats.ScannedFiles.Contains(sKey) || updatedStats.AbandonedFiles.Contains(sKey))
                                _closingReplays.Remove(Path.GetFileName(scannedPath));
                        }
                    }

                    if (run.ActiveMatchFile != null && newestGrowingCandidate == null)
                    {
                        var activeName = Path.GetFileName(run.ActiveMatchFile);
                        bool emitZero = false;
                        lock (_stateLock)
                        {
                            if (!_closingReplays.Contains(activeName) && !_inPlayAgainQueue && _activeReplayName != activeName)
                            {
                                bool switched = _activeReplayName != null;
                                _activeReplayName = activeName;
                                _activeStartUtc = File.GetCreationTimeUtc(run.ActiveMatchFile);
                                if (switched || _liveRaw < 0)
                                {
                                    _liveRaw = 0;
                                    emitZero = true;
                                }
                            }
                        }
                        if (emitZero)
                            LiveChanged?.Invoke(0);
                    }

                    if (run.Parsed > 0 || run.RescuedViaOcr > 0)
                    {
                        bool resetActiveToIdle = false;
                        lock (_stateLock)
                        {
                            if (_activeReplayName != null)
                            {
                                var activeFull = Path.Combine(ChestCounterService.DefaultDemosPath, _activeReplayName);
                                if (File.Exists(activeFull))
                                {
                                    var aKey = ReplayScanner.FileKey(activeFull);
                                    if (updatedStats.ScannedFiles.Contains(aKey) || updatedStats.AbandonedFiles.Contains(aKey))
                                    {
                                        _activeReplayName = null;
                                        _activeSessionId = null;
                                        _liveRaw = -1;
                                        resetActiveToIdle = true;
                                    }
                                }
                            }
                        }

                        if (resetActiveToIdle)
                            LiveChanged?.Invoke(null);

                        var season = updatedStats.Over(ChestCounterService.SeasonStartUtc);
                        StatsUpdated?.Invoke(run, updatedStats);

                        if (run.NewMatches > 0 || run.RescuedViaOcr > 0)
                            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Partida contabilizada! Total na temporada: {season.YourChests:N0} baús ({season.Matches} partidas).");
                        else if (run.UpgradedFromLive > 0)
                            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Replay consolidado com precisão! Total na temporada: {season.YourChests:N0} baús ({season.Matches} partidas).");
                        else if (run.SkippedMode > 0)
                            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Partida fora dos modos do evento (ex: Ranqueada/Criativo/Reload) — ignorada.");
                    }
                }

                // 2) Activate any brand-new growing replay (including the next match started from "Play Again"!)
                if (newestGrowingCandidate != null)
                {
                    string fileName = newestGrowingCandidate.Name;
                    bool shouldActivate = false;
                    bool emitZero = false;
                    DateTime startUtc = default;
                    int liveSnapshot = 0;
                    string? sessionSnapshot = null;
                    string? playlistSnapshot = null;

                    lock (_stateLock)
                    {
                        if (!_inPlayAgainQueue && !_closingReplays.Contains(fileName) && _activeReplayName != fileName)
                        {
                            bool switchedMatch = _activeReplayName != null;
                            _activeReplayName = fileName;
                            _activeStartUtc = newestGrowingCandidate.CreationTimeUtc;
                            if (!string.IsNullOrEmpty(_logWatcher.CurrentSessionId))
                                _activeSessionId = _logWatcher.CurrentSessionId;
                            if (!string.IsNullOrEmpty(_logWatcher.CurrentPlaylistId)
                                && !FortniteLogWatcher.IsFrontendPlaylist(_logWatcher.CurrentPlaylistId))
                            {
                                _activePlaylist = _logWatcher.CurrentPlaylistId;
                            }
                            _lastLiveActivityUtc = nowUtc;

                            if (_liveRaw < 0 || switchedMatch)
                            {
                                _liveRaw = 0;
                                emitZero = true;
                            }

                            startUtc = _activeStartUtc;
                            liveSnapshot = Math.Max(0, _liveRaw);
                            sessionSnapshot = _activeSessionId;
                            playlistSnapshot = _activePlaylist;
                            shouldActivate = true;
                        }
                    }

                    if (shouldActivate)
                    {
                        if (emitZero)
                            LiveChanged?.Invoke(0);
                        LiveSessionJournal.Save(fileName, startUtc, liveSnapshot, sessionSnapshot, playlistSnapshot);
                        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Partida detectada em andamento ({fileName}).");
                    }
                }

                // Auto-clear stale live state if Fortnite was closed and no active replay is growing
                bool clearClosedGame = false;
                lock (_stateLock)
                {
                    if ((_liveRaw >= 0 || _inPlayAgainQueue) && readyToScan.Count == 0
                        && (nowUtc - _lastLiveActivityUtc).TotalMinutes >= 2.5
                        && !IsFortniteRunning())
                    {
                        _activeReplayName = null;
                        _activeSessionId = null;
                        _inPlayAgainQueue = false;
                        _closingReplays.Clear();
                        if (_liveRaw >= 0)
                        {
                            _liveRaw = -1;
                            clearClosedGame = true;
                        }
                    }
                }
                if (clearClosedGame)
                    LiveChanged?.Invoke(null);

                var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in observedLengths.Keys.Where(f => !present.Contains(f)).ToArray())
                    observedLengths.Remove(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"Aguardando liberação do arquivo de replay: {ex.Message}");
            }
            catch { }

            try { await _watchWake.WaitAsync(TimeSpan.FromSeconds(3), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Fortnite shows XP popups right of the crosshair: "+600 XP" with a label under it (BAÚ / CHEST...).
    private static Rectangle PopupArea(Rectangle screen) => new(
        screen.X + (int)(screen.Width * 0.58), screen.Y + (int)(screen.Height * 0.24),
        (int)(screen.Width * 0.28), (int)(screen.Height * 0.24));

    private async Task CaptureLoop(CancellationToken token)
    {
        IntPtr lastWindow = IntPtr.Zero;
        bool gameInFocus = false;

        int credited = 0;      // chests already counted from the popup currently on screen
        int emptyFrames = 0;

        while (!token.IsCancellationRequested)
        {
            // While waiting on the post-match "Play Again" spectator screen, never credit stray UI text to a finished match.
            if (_inPlayAgainQueue)
            {
                credited = 0;
                emptyFrames = 0;
                try { await Task.Delay(350, token); } catch { break; }
                continue;
            }

            var window = GetForegroundWindow();
            if (window != lastWindow)
            {
                gameInFocus = IsGameWindow(window);
                lastWindow = window;
            }
            if (!gameInFocus
                || !GetClientRect(window, out var bounds) || bounds.Right <= 100 || bounds.Bottom <= 100)
            {
                try { await Task.Delay(650, token); } catch { break; }
                continue;
            }

            Point origin = default;
            if (!ClientToScreen(window, ref origin))
            {
                try { await Task.Delay(250, token); } catch { break; }
                continue;
            }

            var area = PopupArea(new Rectangle(origin.X, origin.Y, bounds.Right, bounds.Bottom));
            int n = 0;
            try
            {
                using var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size);
                n = await ChestsInPopup(shot);
            }
            catch { }

            if (token.IsCancellationRequested) return;
            if (n > credited && !_inPlayAgainQueue)
            {
                int next = -1;
                string? replaySnapshot = null;
                DateTime startSnapshot = default;
                string? sessionSnapshot = null;
                string? playlistSnapshot = null;

                lock (_stateLock)
                {
                    if (!_inPlayAgainQueue)
                    {
                        int cur = _liveRaw < 0 ? 0 : _liveRaw;
                        next = cur + (n - credited);
                        _liveRaw = next;
                        _lastLiveActivityUtc = DateTime.UtcNow;
                        replaySnapshot = _activeReplayName;
                        startSnapshot = _activeStartUtc;
                        sessionSnapshot = _activeSessionId;
                        playlistSnapshot = _activePlaylist;
                    }
                }

                credited = n;
                if (next >= 0)
                {
                    if (!string.IsNullOrEmpty(replaySnapshot))
                        LiveSessionJournal.Save(replaySnapshot, startSnapshot, next, sessionSnapshot, playlistSnapshot);

                    LiveChanged?.Invoke(next);
                    Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] Baú detectado ao vivo (nesta partida: {next})");
                }
            }

            emptyFrames = n == 0 ? emptyFrames + 1 : 0;
            if (emptyFrames >= 4) credited = 0;

            try { await Task.Delay(110, token); } catch { break; }
        }
    }

    private static bool IsGameWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        var trimmed = title.ToString().Trim();
        if (trimmed.Equals("Fortnite", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Fortnite ", StringComparison.OrdinalIgnoreCase))
            return true;

        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith("FortniteClient-", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int length);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    private static bool IsFortniteRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
            if (procs.Length > 0)
            {
                foreach (var p in procs) p.Dispose();
                return true;
            }
        }
        catch { }
        return false;
    }

    private string _lastPopup = "";
    private ulong _lastBrightHash;
    private int _lastCachedPopupCount;

    // Fast 0.04ms pre-scan of the raw popup bitmap: avoids 2-3 bicubic GDI+ upscales and WinRT OCR
    // calls on the ~98% of gameplay frames that contain no bright XP popup text at all!
    private static bool HasBrightPopupCandidate(Bitmap area, int minBrightness, out ulong hash)
    {
        hash = 14695981039346656037UL;
        int w = area.Width, h = area.Height;
        if (w < 8 || h < 8) return false;

        int brightPixels = 0;
        var data = area.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y += 2)
                {
                    byte* row = basePtr + y * stride;
                    for (int x = 0; x < w; x += 2)
                    {
                        int px = x * 4;
                        if (row[px] >= minBrightness && row[px + 1] >= minBrightness && row[px + 2] >= minBrightness)
                        {
                            brightPixels++;
                            hash ^= (ulong)((y << 16) ^ x);
                            hash *= 1099511628211UL;
                        }
                    }
                }
            }
        }
        finally
        {
            area.UnlockBits(data);
        }
        return brightPixels >= 14;
    }

    // Multi-pass white-text extraction for high accuracy across SDR/HDR/fade frames.
    public async Task<int> ChestsInPopup(Bitmap area)
    {
        if (!HasBrightPopupCandidate(area, minBrightness: 170, out ulong brightHash))
        {
            _lastBrightHash = 0;
            _lastCachedPopupCount = 0;
            return 0;
        }

        if (brightHash == _lastBrightHash)
            return _lastCachedPopupCount;

        var a = await ReadWhiteText(area, scale: 2, minBrightness: 190);
        bool firstHasXp = a.Any(ChestPopup.HasExperience);
        int firstCount = firstHasXp ? ChestPopup.Count(a) : 0;

        List<string> b = [];
        bool secondHasXp = false;
        int secondCount = 0;

        // Run 3x scale pass when 2x didn't read XP or read XP without a chest label / multiplier check
        if (!firstHasXp || firstCount == 0 || a.Any(l => l.Contains('x', StringComparison.OrdinalIgnoreCase) || l.Contains('×')))
        {
            b = await ReadWhiteText(area, scale: 3, minBrightness: 205);
            secondHasXp = b.Any(ChestPopup.HasExperience);
            secondCount = secondHasXp ? ChestPopup.Count(b) : 0;
        }

        if (!firstHasXp && !secondHasXp)
        {
            // Fallback pass for slightly dimmer / fading XP popups
            var c = await ReadWhiteText(area, scale: 2, minBrightness: 172);
            if (!c.Any(ChestPopup.HasExperience))
            {
                _lastBrightHash = brightHash;
                _lastCachedPopupCount = 0;
                return 0;
            }
            int cCount = ChestPopup.Count(c);
            var cPopup = string.Join(" / ", c);
            if (cPopup != _lastPopup)
            {
                _lastPopup = cPopup;
                PopupRead?.Invoke(cPopup);
            }
            _lastBrightHash = brightHash;
            _lastCachedPopupCount = cCount;
            return cCount;
        }

        var lines = firstHasXp && (!secondHasXp || firstCount >= secondCount) ? a : b;
        var popup = string.Join(" / ", lines);
        if (popup != _lastPopup)
        {
            _lastPopup = popup;
            PopupRead?.Invoke(popup);
        }
        int best = Math.Max(firstCount, secondCount);
        _lastBrightHash = brightHash;
        _lastCachedPopupCount = best;
        return best;
    }

    private async Task<List<string>> ReadWhiteText(Bitmap area, int scale, int minBrightness)
    {
        double factor = Math.Min(scale, (double)OcrEngine.MaxImageDimension / Math.Max(area.Width, area.Height));
        using var big = new Bitmap(Math.Max(1, (int)(area.Width * factor)), Math.Max(1, (int)(area.Height * factor)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(area, new Rectangle(0, 0, big.Width, big.Height), new Rectangle(0, 0, area.Width, area.Height), GraphicsUnit.Pixel);
        }

        int count = checked(big.Width * big.Height);
        var pixels = ArrayPool<byte>.Shared.Rent(checked(count * 4));
        try
        {
            var data = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(data.Scan0, pixels, 0, count * 4); }
            finally { big.UnlockBits(data); }
            for (int i = 0; i < count; i++)
            {
                int src = i * 4;
                bool white = pixels[src] >= minBrightness && pixels[src + 1] >= minBrightness && pixels[src + 2] >= minBrightness;
                pixels[i] = white ? (byte)0 : (byte)255;
            }
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(0, count), BitmapPixelFormat.Gray8, big.Width, big.Height);
            var result = await _engine!.RecognizeAsync(bitmap);
            return result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
    }
}
