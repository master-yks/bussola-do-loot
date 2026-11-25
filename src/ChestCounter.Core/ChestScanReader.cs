using System.Text.RegularExpressions;
using FortniteReplayReader;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using FortniteReplayReader.Models.NetFieldExports.Items;
using FortniteReplayReader.Models.NetFieldExports.Weapons;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestCounter;

public sealed class MateCount
{
    public string Name { get; set; } = "";
    public int Chests { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unnamed { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountKey { get; set; }
}

public sealed class PrizeFind
{
    public string PrizeId { get; set; } = "";
    public string By { get; set; } = "";
    public bool Mine { get; set; }
    public bool Team { get; set; }
}

public sealed class MatchChestCounts
{
    public string MatchId = "";
    public DateTime Time;
    public int You;
    public Dictionary<string, int> YourChestKinds = new();
    public int? Unattributed;
    public string Playlist = "";
    public List<MateCount> Mates = new();
    public List<PrizeFind> Prizes = new();
}

// Reads a Fortnite replay and works out who opened which chest — you, your squad — and which
// treasure-hunt prizes were handed out. Built on the patched parser in /lib (see lib/PATCHES.md);
// the container, refill and ability-grant handling follows LED-N's Fortnite-ChestCounter.
public sealed partial class ChestScanReader : ReplayReader
{
    private sealed record Search(float Time, string Key, string Label, uint? Pawn)
    {
        public float OwnerTime { get; init; } = Time;
        public HashSet<(uint Pawn, float Time)>? OtherPawns { get; set; }
    }
    private sealed record Grant(float Time, uint PlayerState, int ElementId);
    private sealed record Prize(float Time, string Id, uint? Pawn);

    private sealed class PlayerInfo
    {
        public long? PlayerId;   // joins to FortniteReplay.PlayerData, which carries the name
        public string? Account;
        public string? Name;
        public int? Team;
        public bool Spectator;
    }

    // Retained for compatibility with callers that serialize parsing themselves.
    public static readonly object ParseLock = new();

    private const string PrizeChestPath =
        "/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C";

    static ChestScanReader()
    {
        // Any chest or container blueprint the parser does not know is read as a generic container;
        // what kind it is comes from its name (see IsChest).
        NetFieldParser.AliasResolver = path =>
        {
            if (path.Contains(':') || path.EndsWith("_ClassNetCache", StringComparison.Ordinal)) return null;
            var last = path[(path.LastIndexOf('/') + 1)..];
            if (last.StartsWith("BGA_DelilahTreasureHuntAnimatedChest", StringComparison.Ordinal)) return PrizeChestPath;
            if (GizmoActor().IsMatch(path)) return GenericWeapon.GroupPath;
            return path.Contains("/Containers/", StringComparison.OrdinalIgnoreCase)
                   || last.Contains("Chest", StringComparison.OrdinalIgnoreCase)
                ? GenericContainer.GroupPath
                : null;
        };
    }

    private readonly Dictionary<uint, string> _channelKey = new();
    private readonly Dictionary<uint, string> _channelLabel = new();
    private readonly Dictionary<uint, float> _channelOpenAt = new();
    private readonly Dictionary<uint, bool> _channelSearched = new();
    private readonly Dictionary<uint, uint> _lastSearcher = new();
    private readonly Dictionary<string, int> _lastOpening = new();
    private readonly List<Search> _searches = new();
    private readonly Dictionary<uint, (int Index, float Seen)> _pendingOpener = new();   // channel -> search still waiting for its opener

    private readonly Dictionary<uint, List<(float From, uint State)>> _pawnStates = new();
    private readonly Dictionary<string, int> _rosterTeams = new();
    private readonly Dictionary<uint, PlayerInfo> _players = new();   // playerstate actor -> player
    private readonly Dictionary<uint, string> _actorNames = new();

    private readonly List<Prize> _prizes = new();
    private readonly HashSet<uint> _prizeChannels = new();

    // Every treasure a player receives grants a "GA_TreasureHandler_*" ability on their player state.
    private readonly Dictionary<uint, string> _handlerGuids = new();
    private readonly List<Grant> _grants = new();
    private readonly List<(string Name, float Time)> _gizmoSpawns = new();

    private static float Now => CurrentTime;

    public ChestScanReader() : base(null!, ParseMode.Normal)
    {
    }

    public override FortniteReplay ReadReplay(FArchive archive)
    {
        // Bind ThreadStatic RawProbe hooks strictly for the duration of parsing on this worker thread,
        // and always release them in finally so ThreadPool threads never retain ChestScanReader graphs.
        RawProbe.Filter = static path => path.Contains("FortAbilitySystemComponent", StringComparison.Ordinal);
        RawProbe.Sink = (channel, group, field, data, bits) =>
        {
            if (group.Contains("AbilitySystemComponent", StringComparison.Ordinal)
                && group.EndsWith("_ClassNetCache", StringComparison.Ordinal)
                && field is "?" or "ActivatableAbilities" && _handlerGuids.Count > 0)
                ReadAbilityGrants(channel, data, bits);
        };
        try
        {
            return base.ReadReplay(archive);
        }
        finally
        {
            RawProbe.Sink = null;
            RawProbe.Filter = null;
        }
    }

    private string PathOf(uint? guid) =>
        guid is null or 0 ? "" : _netGuidCache.TryGetPathName(guid.Value, out var path) ? path : "";

    [GeneratedRegex("_UAID_.*$")]
    private static partial Regex UaidSuffix();

    private static string CleanName(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        return UaidSuffix().Replace(name, "");
    }

    // Loot cannons, ammo and food boxes are containers but not chests.
    private static bool IsChest(string label)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return label.Contains("chest", comparison) && !label.Contains("cannon", comparison)
            && !label.Contains("ammo", comparison) && !label.Contains("foodbox", comparison)
            && !label.Contains("food_box", comparison);
    }

    // The same container must keep the same key all match: its level path if it is placed in the
    // map, its spawn point if it appeared during the match (buried and relic chests get a new guid
    // every time they come back into range).
    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        base.OnChannelOpened(channelIndex, actor);

        var channel = Channels[channelIndex];
        var archetype = PathOf(channel?.ArchetypeId);
        var actorPath = PathOf(channel?.ActorId);
        var label = CleanName(!string.IsNullOrEmpty(archetype) ? archetype : actorPath);

        if (!string.IsNullOrEmpty(actorPath)) _channelKey[channelIndex] = actorPath;
        else if (channel?.Actor?.Location is { } loc && IsChest(label) && !label.StartsWith("Prop_", StringComparison.OrdinalIgnoreCase))
            _channelKey[channelIndex] = FormattableString.Invariant($"{label}@{loc.X:F0},{loc.Y:F0},{loc.Z:F0}");
        else _channelKey[channelIndex] = $"guid:{channel?.ActorId}";

        _channelLabel[channelIndex] = label;
        _channelOpenAt[channelIndex] = Now;
        _channelSearched.Remove(channelIndex);
        _lastSearcher.Remove(channelIndex);
        _pendingOpener.Remove(channelIndex);
        _prizeChannels.Remove(channelIndex);

        var fullPath = !string.IsNullOrEmpty(archetype) ? archetype : actorPath;
        if (!string.IsNullOrEmpty(fullPath) && GizmoActor().IsMatch(fullPath))
            _gizmoSpawns.Add((CleanName(fullPath), Now));
    }

    private static bool StableKey(string key) =>
        !key.StartsWith("guid:", StringComparison.Ordinal) && !key.StartsWith("ch:", StringComparison.Ordinal);

    // The handler class is exported the first time a treasure is granted; from then on every grant
    // references it by guid. Gizmo items are exported the moment they are first handed out.
    protected override void OnNetGuidExported(uint netGuid, string pathName)
    {
        if (pathName.Contains("TreasureHandler", StringComparison.Ordinal))
            _handlerGuids[netGuid] = CleanName(pathName);
        else if (GizmoItem().IsMatch(pathName) || GizmoActor().IsMatch(pathName))
            _gizmoSpawns.Add((CleanName(pathName), Now));
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        base.OnExportRead(channelIndex, exportGroup);

        switch (exportGroup)
        {
            case BaseContainer container:
                ReadContainer(channelIndex, container);
                break;

            case TreasureHuntChest prize:
                if (prize.bSuccess != false && ReadPrizeId(prize.PrizeTemplateId) is { } id && _prizeChannels.Add(channelIndex))
                    _prizes.Add(new Prize(Now, id, prize.OpeningPawn ?? prize.Instigator));
                break;

            case PlayerPawn pawn:
                if (Channels[channelIndex]?.ActorId is not { } pawnId) break;
                if (pawn.PlayerState is { } binding)
                {
                    if (!_pawnStates.TryGetValue(pawnId, out var history))
                        _pawnStates[pawnId] = history = new();
                    if (history.Count == 0 || history[^1].State != binding)
                        history.Add((Now, binding));
                }
                break;

            case FortPlayerState state:
                if (Channels[channelIndex]?.ActorId is not { } stateId) break;
                if (!_players.TryGetValue(stateId, out var player)) _players[stateId] = player = new PlayerInfo();
                if ((state.PlayerId is null ? state.PlayerID : (int?)state.PlayerId) is { } pid) player.PlayerId = pid;
                if (state.TeamIndex is > 0) player.Team = state.TeamIndex;
                player.Account ??= AccountId(state.UniqueId) ?? AccountId(state.UniqueID) ?? AccountId(state.BotUniqueId);
                if (state.bOnlySpectator is { } spectator) player.Spectator = spectator;
                break;
        }
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
        base.OnNetDeltaRead(channelIndex, update);
        // Deletions in other FastArrays can use the same channel and element index.
        // Only retain complete account/team pairs, never join partial rows across arrays.
        if (!update.Deleted && update.Export is GameMemberInfo { TeamIndex: > 0 } member
            && AccountId(member.MemberUniqueId) is { } account)
        {
            if (_rosterTeams.TryGetValue(account, out var previous) && previous != member.TeamIndex)
                _rosterTeams[account] = 0;
            else _rosterTeams[account] = member.TeamIndex;
        }
    }

    private uint? StateOf(uint? pawn, float time)
    {
        if (pawn == null || !_pawnStates.TryGetValue(pawn.Value, out var history)) return null;
        // Initial replication can name the player after the chest packet. Later possession
        // changes (including an explicit zero) apply only from their own time onwards.
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].From <= time || i == 0)
                return history[i].State == 0 ? null : history[i].State;
        return null;
    }

    // The NetId decoder uses the literal "NULL" for an unspecified network subsystem.
    private static string? AccountId(string? value)
    {
        var id = value?.Trim();
        return string.IsNullOrEmpty(id) || id.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || id.Equals("None", StringComparison.OrdinalIgnoreCase) || id.All(c => c == '0')
            ? null : id.ToUpperInvariant();
    }

    protected override void OnPlayerNameRead(uint channelIndex, PlayerNameData name)
    {
        base.OnPlayerNameRead(channelIndex, name);
        if (Channels[channelIndex]?.ActorId is { } actor && !string.IsNullOrWhiteSpace(name.DecodedName))
            _actorNames[actor] = name.DecodedName.Trim();
    }

    private void ReadContainer(uint channelIndex, BaseContainer container)
    {
        if (!_channelLabel.TryGetValue(channelIndex, out var label) || !IsChest(label)) return;
        var key = _channelKey[channelIndex];
        bool opened = container.bAlreadySearched == true || container.bDestroyed;
        if (container.bAlreadySearched == false && !container.bDestroyed)
        {
            _lastOpening.Remove(key);
            _channelSearched[channelIndex] = false;
            _lastSearcher.Remove(channelIndex);
            _pendingOpener.Remove(channelIndex);
        }

        if (container.SearchingPawn == 0) _lastSearcher.Remove(channelIndex);
        if (container.SearchingPawn is > 0)
        {
            uint pawn = container.SearchingPawn.Value;
            _lastSearcher[channelIndex] = pawn;
            if (_pendingOpener.Remove(channelIndex, out var pending)
                && Now - pending.Seen is >= 0 and <= 2
                && _lastOpening.TryGetValue(key, out var current) && current == pending.Index)
                ObserveOwner(pending.Index, pawn);
        }

        if (!opened) return;
        bool stale = Now - _channelOpenAt[channelIndex] < 0.5f;
        // An initial open snapshot with a transient GUID may repeat a chest from another channel.
        // A recorded closed-to-open transition is evidence even during the first half second.
        bool observedClosed = _channelSearched.TryGetValue(channelIndex, out bool searched) && !searched;
        if (stale && !observedClosed && !StableKey(key) && !_lastOpening.ContainsKey(key))
        {
            _channelSearched[channelIndex] = true;
            return;
        }

        uint? opener = container.SearchingPawn is > 0 ? container.SearchingPawn : null;
        if (container.bAlreadySearched == true && _lastSearcher.TryGetValue(channelIndex, out var last))
            opener = last;

        // A channel can reopen (and replay its state) long after an opening. Only an explicit
        // closed state starts another cycle. Destruction after a search dispenses no extra loot.
        if (_lastOpening.TryGetValue(key, out var prior))
        {
            if (container.bAlreadySearched == true && opener.HasValue)
                ObserveOwner(prior, opener.Value);
            else if (_searches[prior].Pawn == null && container.bAlreadySearched == true)
                _pendingOpener[channelIndex] = (prior, Now);
            _channelSearched[channelIndex] = true;
            return;
        }
        if (_channelSearched.GetValueOrDefault(channelIndex)) return;
        _channelSearched[channelIndex] = true;
        int index = _searches.Count;
        _searches.Add(new Search(Now, key, label, opener));
        _lastOpening[key] = index;
        if (opener == null) _pendingOpener[channelIndex] = (index, Now);
    }

    private void ObserveOwner(int index, uint pawn)
    {
        var opening = _searches[index];
        if (opening.Pawn == null) _searches[index] = opening with { Pawn = pawn, OwnerTime = Now };
        else if (opening.Pawn != pawn || StateOf(pawn, opening.OwnerTime) != StateOf(pawn, Now))
            (opening.OtherPawns ??= new()).Add((pawn, Now));
    }

    // Decodes a FastArray delta (ActivatableAbilities) without field names: header, then for each
    // changed element its id and properties. An element that references a treasure handler class
    // means a treasure was given to the owner of this ability system.
    private void ReadAbilityGrants(uint channel, byte[] data, int bits)
    {
        var pos = 1;   // bSupportsFastArrayDeltaStructSerialization
        int Bit() => pos < bits ? (data[pos >> 3] >> (pos & 7)) & 1 : throw new IndexOutOfRangeException();
        uint Read(int n) { uint v = 0; for (var k = 0; k < n; k++) { v |= (uint)Bit() << k; pos++; } return v; }
        uint ReadPacked()
        {
            uint v = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = Read(8);
                v |= (b >> 1) << shift;
                if ((b & 1) == 0) return v;
            }
            throw new InvalidDataException();
        }

        try
        {
            _ = Read(32); _ = Read(32);   // replication keys
            var deletes = Read(32);
            var changed = Read(32);
            if (deletes > 512 || changed > 512 || bits < 129) return;
            pos += (int)deletes * 32;

            for (var i = 0; i < changed; i++)
            {
                var elementId = (int)Read(32);
                while (true)
                {
                    var handle = ReadPacked();
                    if (handle == 0) break;
                    var numBits = (int)ReadPacked();
                    if (numBits < 0 || pos + numBits > bits) return;
                    if (numBits is not (8 or 16 or 24 or 32 or 40)) { pos += numBits; continue; }

                    var start = pos;
                    uint guid = 0;
                    for (var shift = 0; pos < start + numBits; shift += 7)
                    {
                        var b = Read(8);
                        guid |= (b >> 1) << shift;
                        if ((b & 1) == 0) break;
                    }
                    pos = start + numBits;

                    if (!_handlerGuids.ContainsKey(guid)) continue;
                    var owner = Channels[channel]?.ActorId ?? 0;
                    if (!_grants.Any(g => g.PlayerState == owner && g.ElementId == elementId))
                        _grants.Add(new Grant(Now, owner, elementId));
                }
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException)
        {
            // Not a FastArray delta, or another layout.
        }
    }

    public MatchChestCounts Classify(FortniteReplay replay)
    {
        var result = new MatchChestCounts
        {
            MatchId = MatchId(replay),
            Time = replay.GameData?.UtcTimeStartedMatch ?? DateTime.MinValue,
            Playlist = replay.GameData?.CurrentPlaylist ?? "",
        };

        if (replay.GameData?.RecorderId is not > 0 || !_players.TryGetValue(replay.GameData.RecorderId.Value, out var self))
            throw new InvalidDataException("O replay não identificou o jogador que gravou a partida.");
        uint me = replay.GameData.RecorderId.Value;

        // The playerstate export carries no readable name; the replay's player list does.
        var names = new Dictionary<long, string>();
        foreach (var p in replay.PlayerData ?? [])
            if (p.Id != null && !string.IsNullOrWhiteSpace(p.PlayerName)) names[Convert.ToInt64(p.Id)] = p.PlayerName.Trim();
        foreach (var (actor, p) in _players)
            p.Name = _actorNames.GetValueOrDefault(actor)
                ?? (p.PlayerId is { } pid ? names.GetValueOrDefault(pid) : null);

        int? TeamOf(PlayerInfo who) => who.Team ?? (who.Account != null && _rosterTeams.GetValueOrDefault(who.Account) is > 0 and var team ? team : null);
        int? myTeam = TeamOf(self);

        bool IsMe(uint state) => state == me || (_players.TryGetValue(state, out var who)
            && self.Account != null && who.Account == self.Account);
        bool IsMate(uint state) => !IsMe(state) && myTeam != null && _players.TryGetValue(state, out var who)
            && !who.Spectator && TeamOf(who) == myTeam;

        string Identity(uint state) => _players.TryGetValue(state, out var player) && player.Account != null
            ? "account:" + player.Account : "actor:" + state;
        uint? Opener(Search search)
        {
            if (StateOf(search.Pawn, search.OwnerTime) is not { } state || !_players.ContainsKey(state)) return null;
            if (search.OtherPawns?.Any(e => StateOf(e.Pawn, e.Time) is not { } other || Identity(other) != Identity(state)) == true)
                return null;
            return state;
        }

        var perPlayer = new Dictionary<uint, int>();
        result.Unattributed = 0;
        foreach (var search in _searches)
        {
            if (Opener(search) is not { } state)
            {
                result.Unattributed++;
                continue;
            }
            if (IsMe(state) && ChestKinds.FromActor(search.Label) is { } kind)
                result.YourChestKinds[kind.Key] = result.YourChestKinds.GetValueOrDefault(kind.Key) + 1;
            perPlayer[state] = perPlayer.GetValueOrDefault(state) + 1;
        }
        result.You = perPlayer.Where(p => IsMe(p.Key)).Sum(p => p.Value);

        // Squadmates: same team as the recorder. A player can own more than one playerstate
        // (reconnects). A missing or shared display name must not discard a player's chests.
        if (myTeam != null)
        {
            result.Mates = _players
                .Where(p => IsMate(p.Key))
                .GroupBy(p => !string.IsNullOrEmpty(p.Value.Account) ? "account:" + p.Value.Account
                    : p.Value.PlayerId is { } id ? "player:" + id : "actor:" + p.Key)
                .Select(g =>
                {
                    var name = g.Select(p => p.Value.Name).FirstOrDefault(n => n != null);
                    return new MateCount { Name = name ?? "Colega sem nome", Unnamed = name == null,
                        AccountKey = g.First().Value.Account is { } account ? Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account))) : null,
                        Chests = g.Sum(p => perPlayer.GetValueOrDefault(p.Key)) };
                })
                .OrderByDescending(m => m.Chests)
                .ToList();
        }

        // Golden chests: the prize id and the opener are both in the replay.
        foreach (var prize in _prizes)
        {
            var finder = StateOf(prize.Pawn, prize.Time);
            bool mine = finder is { } player && IsMe(player);
            result.Prizes.Add(new PrizeFind
            {
                PrizeId = prize.Id,
                By = finder is { } f && _players.TryGetValue(f, out var who) ? who.Name ?? "" : "",
                Mine = mine,
                Team = !mine && finder is { } state && IsMate(state),
            });
        }

        // Gizmos: no golden chest, only the handler grant on the recording player. A gizmo goes to
        // everyone in the squad, so each grant is yours; the chest opened at that moment only says
        // whose chest it came out of.
        foreach (var grant in _grants.Where(g => IsMe(g.PlayerState)).OrderBy(g => g.Time))
        {
            if (_prizes.Any(p => Math.Abs(p.Time - grant.Time) <= 5)) continue;

            // A buried chest pays out when the dig completes, a few seconds before it reports "searched".
            var chest = _searches
                .Where(s => s.Time >= grant.Time - 3
                            && (s.Time <= grant.Time + 2 || (s.Time <= grant.Time + 4 && s.Label.Contains("Buried", StringComparison.OrdinalIgnoreCase)))
                            && Opener(s) is { } st && (IsMe(st) || IsMate(st)))
                .OrderBy(s => Math.Abs(s.Time - grant.Time))
                .FirstOrDefault();
            var opener = chest == null ? null : Opener(chest);

            var item = _gizmoSpawns
                .Where(g => Math.Abs(g.Time - grant.Time) <= 3.5f)
                .OrderBy(g => Math.Abs(g.Time - grant.Time))
                .Select(g => g.Name)
                .FirstOrDefault();
            result.Prizes.Add(new PrizeFind
            {
                PrizeId = Treasures.GizmoPrefix + (item ?? ""),
                By = opener is { } mine && IsMe(mine) ? "you" : opener is { } o && _players.TryGetValue(o, out var mate) ? mate.Name ?? "" : "",
                Mine = true,
                Team = true,
            });
        }
        return result;
    }

    [GeneratedRegex("WID_(FlameCeleb|RelicFinder|LuckyLocator|SmugglerExtractionKey)|WID_RelicExtractor(?!_Consumable)")]
    private static partial Regex GizmoItem();

    [GeneratedRegex("(FlameCeleb|RelicFinder|LuckyLocator|SmugglerExtractionKey|RelicExtractor)(?!.*_Consumable)")]
    private static partial Regex GizmoActor();

    // "DelilahPrize:dpid_item4", or a bare GUID for the Pirate Shop Access token.
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex PrizeIdPattern();

    private static string? ReadPrizeId(DebuggingObject? raw)
    {
        if (raw == null || raw.TotalBits == 0) return null;
        var ascii = new string(raw.Bytes.Select(b => b is >= 32 and < 127 ? (char)b : ' ').ToArray());
        var match = PrizeIdPattern().Match(ascii);
        return match.Success ? match.Value : null;
    }

    private static string MatchId(FortniteReplay replay)
    {
        var gd = replay.GameData;
        if (!string.IsNullOrEmpty(gd?.GameSessionId)) return gd!.GameSessionId;
        return gd?.UtcTimeStartedMatch is { } start && start != DateTime.MinValue
            ? $"{start:O}|{gd.RecorderId}" : "";
    }
}
