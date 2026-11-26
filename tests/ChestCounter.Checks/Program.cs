using System.Reflection;
using ChestCounter;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using FortniteReplayReader.Models.NetFieldExports.Items;
using Unreal.Core.Models;

static void Check(bool ok, string title)
{
    if (!ok) throw new InvalidOperationException(title);
    Console.WriteLine("OK " + title);
}

if (args.Length > 0)
{
    // Read-only integration check on the user's own .replay files. All stats stay in memory.
    var files = ChestCounterService.FindReplays(args[0]);
    Check(files.Count > 0, "replay directory contains files");
    var realStats = new LifetimeStats();
    var scanner = new ReplayScanner();
    var scan = scanner.Scan(files, realStats, (_, _, name, err) => { if (err != null) Console.Error.WriteLine($"{name}: {err}"); });
    var again = scanner.Scan(files, realStats);
    Console.WriteLine($"Files={files.Count} matches={realStats.Matches} unfinished={scan.AbandonedCrash + scan.ActiveRecording} skipped={scan.SkippedMode} failed={scan.Failed}");
    Console.WriteLine($"Self={realStats.YourChests} mates={realStats.SquadChests - realStats.YourChests} unknown (all teams)={realStats.Records.Sum(r => r.Unattributed)}");
    Check(realStats.Records.Select(r => r.MatchId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == realStats.Matches,
        "unique match IDs in history");
    Check(realStats.Records.All(r => r.You >= 0 && r.UntypedChests >= 0 && r.YourChestKinds.Values.All(n => n >= 0)),
        "typed chest counts fit within each personal total");
    Check(scan.Failed == 0 && realStats.Matches > 0, "read real replays");
    Check(again.NewMatches == 0 && again.Parsed == 0 && again.Failed == 0, "warm scan is idempotent");
    foreach (var record in realStats.Records.Where(r => r.Mates.Count > 0 && r.Source == "replay").Take(3))
    {
        var independent = new ChestScanReader();
        var match = independent.Classify(independent.ReadReplay(Path.Combine(args[0], record.ReplayFile!)));
        Check(match.You == record.You && match.Unattributed == record.Unattributed
            && match.Mates.Select(m => (m.Name, m.Chests)).SequenceEqual(record.Mates.Select(m => (m.Name, m.Chests))),
            "serial and parallel squad counts agree");
    }
    return;
}

var reader = new ChestScanReader();
var type = reader.GetType();
const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
typeof(FortniteReplayReader.ReplayReader).GetField("Builder", flags)!.SetValue(reader, new FortniteReplayReader.FortniteReplayBuilder());
T Field<T>(string key) => (T)type.GetField(key, flags)!.GetValue(reader)!;
var channels = Field<UChannel[]>("Channels");
var read = type.GetMethod("OnExportRead", flags)!;
var open = type.GetMethod("OnChannelOpened", flags)!;
void Bind(uint ch, uint actor)
{
    var guid = new NetworkGUID { Value = actor };
    channels[ch] = new UChannel { Actor = new Actor { ActorNetGUID = guid } };
    open.Invoke(reader, [ch, guid]);
}
void Player(uint actor, int team, string? name, string? account = null, bool spectator = false)
{
    Bind(actor, actor);
    read.Invoke(reader, [actor, new FortPlayerState { TeamIndex = team, PlayerId = actor, UniqueId = account ?? "", bOnlySpectator = spectator }]);
    if (name != null) Field<Dictionary<uint,string>>("_actorNames")[actor] = name;
    Bind(actor + 100, actor + 100);
    read.Invoke(reader, [actor + 100, new PlayerPawn { PlayerState = actor }]);
}
void ChestChannel(uint ch, string key, float time)
{
    Unreal.Core.ReplayReader<FortniteReplay>.CurrentTime = time;
    Bind(ch, ch);
    Field<Dictionary<uint,string>>("_channelKey")[ch] = key;
    Field<Dictionary<uint,string>>("_channelLabel")[ch] = "Tiered_Chest";
}
void Event(uint ch, float time, bool? searched, bool destroyed, uint? pawn)
{
    Unreal.Core.ReplayReader<FortniteReplay>.CurrentTime = time;
    read.Invoke(reader, [ch, new Chest { bAlreadySearched = searched, bDestroyed = destroyed, SearchingPawn = pawn }]);
}
var replay = new FortniteReplay();
replay.GameData.RecorderId = 1;
Player(1, 5, "Eu", "NULL");
Player(2, 5, "Colega", "AbC");
Player(3, 5, null, "NULL");
Player(4, 8, "Adversário", "NULL");
Player(5, 5, "Eu"); // another human with the same display name
ChestChannel(21, "A", 1);
Event(21, 3, true, false, 102);
ChestChannel(22, "A", 6);
Event(22, 6, true, false, null);
ChestChannel(23, "A", 10);
Event(23, 10, true, false, 102);
Event(23, 12, false, false, null);
Event(23, 15, true, false, 102);
ChestChannel(24, "B", 18);
Event(24, 21, null, true, null);
Event(24, 21.1f, null, false, 103);
ChestChannel(25, "C", 25);
Event(25, 30, true, false, 104);
ChestChannel(26, "D", 35);
Event(26, 40, true, false, 105);
ChestChannel(27, "E", 45);
Event(27, 47, true, false, 101);
var counts = reader.Classify(replay);
Check(counts.You == 1 && counts.Mates.Count == 3, "self and team membership");
Check(counts.Mates.Single(m => m.Name == "Colega").Chests == 2, "duplicate observations and refill");
Check(counts.Mates.Single(m => m.Unnamed).Chests == 1, "late owner on destruction and unnamed teammate");
Check(counts.Mates.Single(m => m.Name == "Eu").Chests == 1, "shared display name");

Player(6, 5, "Colega", "abc");  // reconnect
Player(7, 5, "Espectador", spectator: true);
ChestChannel(30, "A", 50);
Event(30, 55, true, false, 106); // delayed snapshot of same cycle, new pawn for same account
ChestChannel(31, "F", 56);
Event(31, 57, true, false, 102);
Event(31, 57.1f, true, false, 104); // conflicting observer evidence
ChestChannel(32, "G", 58);
Event(32, 59, null, true, null); // nobody identified
Event(32, 65, null, false, 101); // unrelated late update
ChestChannel(33, "H", 70);
Event(33, 71, true, false, 107);
ChestChannel(34, "I", 72);
Event(34, 73, true, false, 106);
ChestChannel(35, "J", 74);
Field<Dictionary<uint,string>>("_channelLabel")[35] = "AmmoBox";
Event(35, 75, true, false, 101);
counts = reader.Classify(replay);
Check(counts.You == 1 && counts.Mates.Count == 3 && counts.Mates.Sum(m => m.Chests) == 5,
    "reconnect, placeholder accounts, enemy, spectator and ammo exclusion");
Check(counts.Mates.Single(m => m.Name == "Colega").Chests == 3 && counts.Unattributed == 2,
    "unattributed conflict and unowned destruction, no duplicate on delayed snapshot");

Player(8, 0, "Roster", "cba");
type.GetMethod("OnNetDeltaRead", flags)!.Invoke(reader, [9u, new NetDeltaUpdate
    { Export = new GameMemberInfo { MemberUniqueId = "CBA", TeamIndex = 5 } }]);
ChestChannel(36, "K", 76);
Event(36, 77, true, false, 108);
Unreal.Core.ReplayReader<FortniteReplay>.CurrentTime = 80;
read.Invoke(reader, [108u, new PlayerPawn { PlayerState = 4 }]);
ChestChannel(37, "L", 81);
Event(37, 82, true, false, 108);
counts = reader.Classify(replay);
Check(counts.Mates.Single(m => m.Name == "Roster").Chests == 1 && counts.Mates.Sum(m => m.Chests) == 6,
    "roster fallback and time of pawn possession");

// Instance suffixes and order matter. An unidentified blueprint still contributes to You.
foreach (var (actor, kind) in new (string, ChestKind?)[]
{
    ("Tiered_Chest_Athena47", ChestKinds.Common),
    ("AlwaysSpawn_NormalChest_C_213c070f64b695f3_4", ChestKinds.Common),
    ("AlwaysSpawn_RareChest_C", ChestKinds.Rare),
    ("B_Athena_BuriedChest_Chest_C", ChestKinds.Buried),
    ("B_TieredChest_Relics_C", ChestKinds.Relic),
    ("BuriedSpriteChest_C", ChestKinds.Poster),
    ("Tiered_Chest_Athena_FutureSeason_C", null)
})
    Check(ChestKinds.FromActor(actor) == kind, $"container family {actor}");

ChestChannel(40, "Rare", 84);
Field<Dictionary<uint,string>>("_channelLabel")[40] = "AlwaysSpawn_RareChest_C";
Event(40, 85, true, false, 101);
Event(40, 86, true, false, 101);
ChestChannel(41, "Unknown", 87);
Field<Dictionary<uint,string>>("_channelLabel")[41] = "FutureChest_C";
Event(41, 88, true, false, 101);
counts = reader.Classify(replay);
Check(counts.You == 3 && counts.YourChestKinds.Count == 2
    && counts.YourChestKinds["common"] == 1 && counts.YourChestKinds["rare"] == 1,
    "typed personal counts exclude teammates and repeated observations");
Check(new MatchRecord { You = 9 }.UntypedChests == 9, "historical and OCR totals keep unknown type");

ChestChannel(42, "guid:500", 90);
Event(42, 90, false, false, null);
Event(42, 90.1f, true, false, 101);
Event(42, 90.2f, true, false, 101);
ChestChannel(43, "guid:501", 91);
Event(43, 91, true, false, 101);
Check(reader.Classify(replay).You == counts.You + 1, "fast observed opening counts once, initial transient snapshot stays excluded");

var log = new FortniteLogWatcher();
int queued = 0;
log.PlayAgainTriggered += _ => queued++;
void Line(string text) => log.ProcessLine(text, true);
Line("Metadata set : playlistid=\"Playlist_DefaultSquad\"");
Line("Metadata set : sessionid=\"abc123\"");
Line("LogIGMWidgets: UAthenaReadyUpScreenOverlay::Activate()");
Line("Metadata set : sessionid=\"ABC123\"");
Line("Starting matchmaking attempt 2");
Check(queued == 1 && log.IsPlayAgainActive, "repeated ready-up and session metadata");
Line("LogDemo: StopDemo:");
Line("Metadata set : sessionid=\"next\"");
Check(log.IsInMatch && !log.IsPlayAgainActive, "next match without lobby");
var stats = new LifetimeStats();
stats.AddMatch(new MatchChestCounts { MatchId = "ABC123", You = 6 }, "play-again", "m.replay");
counts.MatchId = "abc123";
stats.AddMatch(counts, "replay", "m.replay");
stats.AddMatch(new MatchChestCounts { MatchId = "ABC123", You = 6 }, "play-again", "m.replay");
Check(stats.Matches == 1 && stats.Records[0].Source == "replay" && stats.YourChests == counts.You && stats.Records[0].Mates.Count == 4,
    "replay replaces estimate once and retains team");
var reused = new LifetimeStats();
reused.AddMatch(new MatchChestCounts { MatchId = "session-older", You = 1 }, "play-again", "slot.replay");
reused.AddMatch(new MatchChestCounts { MatchId = "session-newer", You = 2 }, "replay", "slot.replay");
Check(reused.Matches == 2 && reused.YourChests == 3, "rotated filename with different session IDs");
Check(!reused.RemovePendingPlayAgain("slot.replay", "session-skipped") && reused.Matches == 2,
    "skipping another mode preserves a pending match with a different session ID");
Check(reused.RemovePendingPlayAgain("slot.replay", "session-older") && reused.Matches == 1,
    "skipping the matching session removes its estimate only");

reused.AddMatch(new MatchChestCounts { MatchId = "session-rescued", You = 3 }, "ocr-rescue", "slot.replay");
Check(!reused.HasReplayRecord("slot.replay", "next-session"), "rotated filename cannot suppress a new rescue");
Check(reused.HasReplayRecord("renamed.replay", "session-rescued"), "rescue identified by session after rename");
Check(reused.RemoveEstimates("slot.replay", "session-rescued") && reused.Matches == 1 && reused.YourChests == 2,
    "excluded mode removes rescued OCR and preserves confirmed session");

var namesakes = new LifetimeStats();
namesakes.AddMatch(new MatchChestCounts { MatchId = "one", Mates = [
    new() { Name = "Mesmo", AccountKey = "alpha", Chests = 2 },
    new() { Name = "Mesmo", AccountKey = "beta", Chests = 3 }] });
namesakes.AddMatch(new MatchChestCounts { MatchId = "two", Mates = [
    new() { Name = "Novo nome", AccountKey = "alpha", Chests = 1 }] });
var ranked = namesakes.TopMates(null, 10);
Check(ranked.Count == 2 && ranked[0] == new MateTotal("Novo nome", 2, 3), "account-based ranking across aliases");
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
try
{
    stats.Version = LifetimeStats.CurrentVersion - 1;
    stats.ScannedFiles.Add("m.replay|10");
    StatsStore.Save(path,stats);
    var migrated = StatsStore.Load(path);
    Check(migrated.Records.Single().UntypedChests == 1 && migrated.Records.Single().CountOf(ChestKinds.Rare) == 1, "typed counts survive consolidation and save/load");
    Check(migrated.Matches == 1 && migrated.ScannedFiles.Count == 0 && migrated.Version == LifetimeStats.CurrentVersion && migrated.Records[0].Unattributed == 2, "retains history and enables rescan");
}
finally { File.Delete(path); }

int freePort;
using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
    probe.Start();
    freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
}
using (var overlay = new OverlayServer(freePort)
{
    ConfigProvider = () => "{\"layout\":\"tactical\"}",
    StatsBuilder = live => $"{{\"live\":{(live?.ToString() ?? "null")}}}",
})
{
    overlay.Start();
    using var http = new System.Net.Http.HttpClient();
    string baseUrl = $"http://127.0.0.1:{freePort}";
    var html = http.GetStringAsync($"{baseUrl}/").GetAwaiter().GetResult();
    var cfg = http.GetStringAsync($"{baseUrl}/config.json").GetAwaiter().GetResult();
    var png = http.GetByteArrayAsync($"{baseUrl}/chest.png").GetAwaiter().GetResult();
    var post = http.PostAsync($"{baseUrl}/live?n=9", null).GetAwaiter().GetResult();
    var st = http.GetStringAsync($"{baseUrl}/stats.json").GetAwaiter().GetResult();
    var notFound = http.GetAsync($"{baseUrl}/missing").GetAwaiter().GetResult();
    Check(html.Contains("Bússola do Loot") && cfg == "{\"layout\":\"tactical\"}" && png.Length > 100
        && post.IsSuccessStatusCode && st == "{\"live\":9}" && (int)notFound.StatusCode == 404,
        "local overlay HTTP server endpoints");
}
