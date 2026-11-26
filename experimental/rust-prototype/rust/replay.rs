//! Fortnite Replay Reader & Chest Scanner in pure Rust.
//! Implements all 21 patches from `lib/PATCHES.md` plus `ChestScanReader` attribution logic.
//! Supports both finished replays and live/incremental replay scanning without any global locks.

use crate::bit_reader::{
    BitReader, ByteReader, EndIndex, FRepMovement, FRotator, FVector, NetworkReplayVersion,
    RotatorQuantization, VectorQuantization,
};
use crate::oodle::decompress_replay_data;
use crate::stats::{LifetimeStats, MateCount, MatchChestCounts, PrizeFind};
use crate::treasures::GIZMO_PREFIX;
use aes::Aes256;
use aes::cipher::{BlockDecryptMut, KeyInit, block_padding::Pkcs7};
use anyhow::{Result, bail};
use chrono::{DateTime, TimeZone, Utc};
use rayon::prelude::*;
use regex::Regex;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

const DEFAULT_MAX_CHANNEL_SIZE: usize = 32767;
const FILE_MAGIC: u32 = 0x1CA2E27F;
const NETWORK_MAGIC: u32 = 0x2CF5A13D;
const MAX_PACKET_SIZE_IN_BITS: u32 = 16384;
const OLD_MAX_ACTOR_CHANNELS: u32 = 10240;
const MAX_GUID_COUNT: i32 = 2048;

const PRIZE_CHEST_PATH: &str = "/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C";
const GENERIC_CONTAINER_PATH: &str = "__ChestCounter.GenericContainer";
const GENERIC_VEHICLE_PATH: &str = "__ChestCounter.GenericVehicle";

static UAID_SUFFIX_RE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"_UAID_.*$").unwrap());
static STATIC_INSTANCE_SUFFIX_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(_UAID_.*|_[0-9a-f]{16}_\d+)$").unwrap());
static VEHICLE_CLASS_RE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"_Vehicle(_Static)?_C$|MeatballVehicle").unwrap());
static GIZMO_ITEM_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"WID_(FlameCeleb|RelicFinder|LuckyLocator|SmugglerExtractionKey)|WID_RelicExtractor")
        .unwrap()
});
static PRIZE_ID_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
        .unwrap()
});

pub fn remove_all_path_prefixes(path: &str) -> &str {
    let bytes = path.as_bytes();
    for i in (0..bytes.len()).rev() {
        match bytes[i] {
            b'.' => return &path[i + 1..],
            b'/' => return path,
            _ => {}
        }
    }
    path.strip_prefix("Default__").unwrap_or(path)
}

pub fn clean_path_suffix(path: &str) -> &str {
    let bytes = path.as_bytes();
    for i in (0..bytes.len()).rev() {
        if !bytes[i].is_ascii_digit() && bytes[i] != b'_' {
            return &path[..i + 1];
        }
    }
    path
}

pub fn clean_name(path: &str) -> String {
    let after_slash = match path.rfind('/') {
        Some(idx) => &path[idx + 1..],
        None => path,
    };
    let after_dot = match after_slash.rfind('.') {
        Some(idx) => &after_slash[idx + 1..],
        None => after_slash,
    };
    UAID_SUFFIX_RE.replace(after_dot, "").to_string()
}

pub fn is_chest(label: &str) -> bool {
    let b = label.to_ascii_lowercase();
    if b.contains("cannon")
        || b.contains("ammo")
        || b.contains("foodbox")
        || b.contains("food_box")
    {
        return false;
    }
    b.contains("chest")
}

fn is_gizmo_item(path: &str) -> bool {
    if path.contains("WID_RelicExtractor_Consumable") {
        return false;
    }
    GIZMO_ITEM_RE.is_match(path)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum KnownGroup {
    GenericContainer,
    TreasureHuntChest,
    PlayerPawn,
    GenericVehicle,
    SeatComponent,
    FortPickup,
    FortPlayerState,
    GameState,
    PlaylistInfo,
    ActiveGameplayModifier,
    ReplayPC,
    SupplyDropLlama,
    SupplyDrop,
    FortPoiManager,
    SafeZoneIndicator,
    BaseWeapon,
}

fn resolve_known_group(group_path: &str) -> Option<KnownGroup> {
    match group_path {
        GENERIC_CONTAINER_PATH => return Some(KnownGroup::GenericContainer),
        PRIZE_CHEST_PATH => return Some(KnownGroup::TreasureHuntChest),
        GENERIC_VEHICLE_PATH => return Some(KnownGroup::GenericVehicle),
        "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C" => return Some(KnownGroup::PlayerPawn),
        "/Script/FortniteGame.FortVehicleSeatComponent" => return Some(KnownGroup::SeatComponent),
        "/Script/FortniteGame.FortPickupAthena" => return Some(KnownGroup::FortPickup),
        "/Script/FortniteGame.FortPlayerStateAthena" => return Some(KnownGroup::FortPlayerState),
        "/Game/Athena/Athena_GameState.Athena_GameState_C" => return Some(KnownGroup::GameState),
        "CurrentPlaylistInfo" => return Some(KnownGroup::PlaylistInfo),
        "/Script/FortniteGame.ActiveGameplayModifier" => {
            return Some(KnownGroup::ActiveGameplayModifier)
        }
        "/Game/Spectating/BP_ReplayPC_Athena.BP_ReplayPC_Athena_C" => {
            return Some(KnownGroup::ReplayPC)
        }
        "/Game/Athena/SupplyDrops/Llama/AthenaSupplyDrop_Llama.AthenaSupplyDrop_Llama_C" => {
            return Some(KnownGroup::SupplyDropLlama)
        }
        "/Game/Athena/SupplyDrops/AthenaSupplyDrop.AthenaSupplyDrop_C" => {
            return Some(KnownGroup::SupplyDrop)
        }
        "/Script/FortniteGame.FortPoiManager" => return Some(KnownGroup::FortPoiManager),
        "/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C" => {
            return Some(KnownGroup::SafeZoneIndicator)
        }
        _ => {}
    }

    // AliasResolver (Patch 2 & ChestScanReader static constructor)
    if group_path.contains(':') || group_path.ends_with("_ClassNetCache") {
        return None;
    }
    let last = match group_path.rfind('/') {
        Some(idx) => &group_path[idx + 1..],
        None => group_path,
    };
    if last.starts_with("BGA_DelilahTreasureHuntAnimatedChest") {
        return Some(KnownGroup::TreasureHuntChest);
    }
    if VEHICLE_CLASS_RE.is_match(last) {
        return Some(KnownGroup::GenericVehicle);
    }
    let lower_path = group_path.to_ascii_lowercase();
    let lower_last = last.to_ascii_lowercase();
    if lower_path.contains("/containers/") || lower_last.contains("chest") {
        return Some(KnownGroup::GenericContainer);
    }
    if group_path.starts_with("/Game/Weapons/")
        || group_path.starts_with("/Game/Athena/Items/Weapons/")
    {
        return Some(KnownGroup::BaseWeapon);
    }
    None
}

#[derive(Clone, Debug)]
pub struct NetFieldExport {
    pub handle: u32,
    pub compatible_checksum: u32,
    pub name: String,
    pub incompatible: bool,
}

#[derive(Clone, Debug)]
pub struct NetFieldExportGroup {
    pub path_name: String,
    pub path_name_index: u32,
    pub exports: Vec<Option<NetFieldExport>>,
}

impl NetFieldExportGroup {
    pub fn is_valid_index(&self, handle: u32) -> bool {
        (handle as usize) < self.exports.len()
    }
}

#[derive(Clone, Debug, Default)]
pub struct NetGuidCache {
    pub export_group_map: HashMap<String, NetFieldExportGroup>,
    pub group_order: Vec<String>,
    pub index_to_group_name: HashMap<u32, String>,
    pub guid_to_path_name: HashMap<u32, String>,
    pub external_data: HashMap<u32, Vec<u8>>,
    pub arch_to_group_name: HashMap<u32, String>,
    pub cleaned_paths: HashMap<u32, String>,
    pub failed_paths: HashSet<String>,
}

impl NetGuidCache {
    pub fn clear_export_groups(&mut self) {
        self.export_group_map.clear();
        self.group_order.clear();
        self.index_to_group_name.clear();
        self.arch_to_group_name.clear();
    }

    pub fn add_to_export_group_map(&mut self, group_name: String, mut group: NetFieldExportGroup) {
        if group_name.ends_with("ClassNetCache") {
            group.path_name = remove_all_path_prefixes(&group.path_name).to_string();
        }
        // Patch 15: clear failed_paths whenever a new export group is added
        if !self.export_group_map.contains_key(&group_name) {
            self.failed_paths.clear();
            self.group_order.push(group_name.clone());
        }
        let cleaned = remove_all_path_prefixes(&group_name).to_string();
        self.cleaned_paths.insert(group.path_name_index, cleaned);
        self.index_to_group_name
            .insert(group.path_name_index, group_name.clone());
        self.export_group_map.insert(group_name, group);
    }

    pub fn get_group_from_index(&self, index: u32) -> Option<&NetFieldExportGroup> {
        let name = self.index_to_group_name.get(&index)?;
        self.export_group_map.get(name)
    }

    pub fn get_group_from_index_mut(&mut self, index: u32) -> Option<&mut NetFieldExportGroup> {
        let name = self.index_to_group_name.get(&index)?.clone();
        self.export_group_map.get_mut(&name)
    }

    pub fn get_group_by_guid(&mut self, netguid: Option<u32>) -> Option<String> {
        let guid = netguid?;
        if let Some(found) = self.arch_to_group_name.get(&guid) {
            return Some(found.clone());
        }
        let path = self.guid_to_path_name.get(&guid)?.clone();
        if self.failed_paths.contains(&path) {
            return None;
        }

        for group_path in &self.group_order {
            if let Some(grp) = self.export_group_map.get(group_path) {
                if let Some(fixed) = self.cleaned_paths.get(&grp.path_name_index) {
                    if path.contains(fixed.as_str()) {
                        self.arch_to_group_name.insert(guid, group_path.clone());
                        return Some(group_path.clone());
                    }
                }
            }
        }

        let cleaned_path = clean_path_suffix(&path);
        for group_path in &self.group_order {
            if let Some(grp) = self.export_group_map.get(group_path) {
                if let Some(fixed) = self.cleaned_paths.get(&grp.path_name_index) {
                    if fixed.contains(cleaned_path) {
                        self.arch_to_group_name.insert(guid, group_path.clone());
                        return Some(group_path.clone());
                    }
                }
            }
        }

        // Patch 12: level placed actors whose instance name is not their class name
        // e.g. Tiered_Chest_Athena47_UAID_... or Tiered_Chest_Athena5_6e02a0a859c897db_11
        let stripped = STATIC_INSTANCE_SUFFIX_RE.replace(remove_all_path_prefixes(&path), "");
        let base_name = clean_path_suffix(&stripped);
        if !base_name.is_empty() {
            let class_name = format!("{}_C", base_name);
            for group_path in &self.group_order {
                if let Some(grp) = self.export_group_map.get(group_path) {
                    if let Some(fixed) = self.cleaned_paths.get(&grp.path_name_index) {
                        if fixed == &class_name {
                            self.arch_to_group_name.insert(guid, group_path.clone());
                            return Some(group_path.clone());
                        }
                    }
                }
            }
        }

        self.failed_paths.insert(path);
        None
    }

    pub fn get_class_net_cache_name(&self, group: &str, use_full_name: bool) -> Option<String> {
        if group.is_empty() {
            return None;
        }
        let target = if use_full_name {
            format!("{}_ClassNetCache", group)
        } else {
            format!("{}_ClassNetCache", remove_all_path_prefixes(group))
        };
        if self.export_group_map.contains_key(&target) {
            Some(target)
        } else {
            None
        }
    }

    pub fn try_get_tag_name(&self, tag_index: u32) -> Option<String> {
        let grp = self
            .export_group_map
            .get("NetworkGameplayTagNodeIndex")
            .or_else(|| self.export_group_map.get("NetworkGameplayTagDynamicIndex"))?;
        let exp = grp.exports.get(tag_index as usize)?.as_ref()?;
        Some(exp.name.clone())
    }
}

#[derive(Clone, Debug, Default)]
pub struct ReplayInfo {
    pub file_version: u32,
    pub length_in_ms: u32,
    pub network_version: u32,
    pub changelist: u32,
    pub friendly_name: String,
    pub is_live: bool,
    pub timestamp: Option<DateTime<Utc>>,
    pub is_compressed: bool,
    pub is_encrypted: bool,
    pub encryption_key: Vec<u8>,
}

#[derive(Clone, Debug, Default)]
pub struct ReplayHeader {
    pub network_version: u32,
    pub network_checksum: u32,
    pub engine_network_version: u32,
    pub game_network_protocol_version: u32,
    pub guid: String,
    pub major: u16,
    pub minor: u16,
    pub patch: u16,
    pub changelist: u32,
    pub branch: String,
    pub flags: u32,
    pub platform: String,
}

#[derive(Clone, Debug)]
pub struct Actor {
    pub actor_net_guid: u32,
    pub archetype: Option<u32>,
    pub level: Option<u32>,
    pub location: Option<FVector>,
    pub rotation: FRotator,
    pub scale: FVector,
    pub velocity: FVector,
}

#[derive(Clone, Debug)]
pub struct UChannel {
    pub channel_index: u32,
    pub actor: Option<Actor>,
    pub ignore_groups: HashSet<String>,
}

impl UChannel {
    pub fn new(channel_index: u32) -> Self {
        Self {
            channel_index,
            actor: None,
            ignore_groups: HashSet::new(),
        }
    }

    pub fn archetype_id(&self) -> Option<u32> {
        self.actor.as_ref().and_then(|a| a.archetype)
    }

    pub fn actor_id(&self) -> Option<u32> {
        self.actor.as_ref().map(|a| a.actor_net_guid)
    }
}

#[derive(Clone, Debug, Default)]
pub struct DataBunch {
    pub archive: BitReader,
    pub packet_id: i32,
    pub ch_index: u32,
    pub ch_sequence: i32,
    pub b_open: bool,
    pub b_close: bool,
    pub b_dormant: bool,
    pub b_is_replication_paused: bool,
    pub b_reliable: bool,
    pub b_partial: bool,
    pub b_partial_initial: bool,
    pub b_partial_final: bool,
    pub b_has_partial_custom_exports_final_bit: bool,
    pub b_has_package_map_exports: bool,
    pub b_has_must_be_mapped_guids: bool,
    pub close_reason: u32,
}

// ---- Exported group structures ----

#[derive(Clone, Debug, Default)]
pub struct BaseContainerData {
    pub b_destroyed: bool,
    pub b_instant_death: bool,
    pub searching_pawn: Option<u32>,
    pub b_already_searched: Option<bool>,
    pub search_animation_count: Option<u32>,
}

#[derive(Clone, Debug, Default)]
pub struct TreasureHuntChestData {
    pub instigator: Option<u32>,
    pub source_container: Option<u32>,
    pub opening_pawn: Option<u32>,
    pub b_success: Option<bool>,
    pub prize_template_bytes: Option<Vec<u8>>,
}

#[derive(Clone, Debug, Default)]
pub struct PlayerPawnData {
    pub player_state: Option<u32>,
    pub replicated_movement: Option<FRepMovement>,
}

#[derive(Clone, Debug, Default)]
pub struct GenericVehicleData {
    pub replicated_movement: Option<FRepMovement>,
}

#[derive(Clone, Debug, Default)]
pub struct SeatComponentData {
    pub player_slots: Option<Vec<Option<u32>>>,
    pub player: Option<u32>,
}

#[derive(Clone, Debug, Default)]
pub struct FortPickupData {
    pub owner: Option<u32>,
    pub b_tossed_from_container: Option<bool>,
}

#[derive(Clone, Debug, Default)]
pub struct FortPlayerStateData {
    pub player_id_i32: Option<i32>,
    pub player_id_u32: Option<u32>,
    pub team_index: Option<i32>,
    pub b_only_spectator: Option<bool>,
    pub b_is_a_bot: Option<bool>,
}

#[derive(Clone, Debug, Default)]
pub struct GameStateData {
    pub game_session_id: Option<String>,
    pub utc_time_started_match: Option<u64>,
    pub recorder_player_state: Option<u32>,
}

// ---- ChestScanReader tracking structures ----

#[derive(Clone, Debug)]
struct Search {
    time: f32,
    key: String,
    label: String,
    pawn: Option<u32>,
}

#[derive(Clone, Debug)]
struct Grant {
    time: f32,
    player_state: u32,
    element_id: i32,
}

#[derive(Clone, Debug)]
struct Prize {
    time: f32,
    id: String,
    pawn: Option<u32>,
}

#[derive(Clone, Copy, Debug)]
struct Pt {
    t: f32,
    x: f32,
    y: f32,
    z: f32,
}

#[derive(Clone, Debug)]
struct Orphan {
    time: f32,
    key: String,
    label: String,
    actor: u32,
}

#[derive(Clone, Debug)]
struct SeatSpan {
    pawn: u32,
    vehicle: u32,
    from: f32,
    to: f32,
}

#[derive(Clone, Debug, Default)]
struct PlayerInfo {
    player_id: Option<i64>,
    name: Option<String>,
    team: Option<i32>,
}

#[derive(Clone, Debug, Default)]
struct BuilderPlayerData {
    id: Option<i32>,
    player_name: Option<String>,
}

pub struct ChestScanReader {
    pub info: ReplayInfo,
    pub header: ReplayHeader,
    pub is_partial_replay: bool,
    pub current_time: f32,
    pub guid_cache: NetGuidCache,
    pub channels: Vec<Option<UChannel>>,
    pub in_reliable: i32,
    pub in_packet_id: i32,
    pub partial_bunch: Option<DataBunch>,

    // GameData / Builder state
    pub game_session_id: Option<String>,
    pub utc_time_started_match: Option<DateTime<Utc>>,
    pub recorder_id: Option<u32>,
    pub current_playlist: Option<String>,
    builder_players: HashMap<u32, BuilderPlayerData>, // channelIndex -> BuilderPlayerData
    only_spectating_channels: HashSet<u32>,

    // ChestScanReader tracking
    channel_key: HashMap<u32, String>,
    channel_label: HashMap<u32, String>,
    channel_open_at: HashMap<u32, f32>,
    channel_searched: HashMap<u32, bool>,
    last_searcher: HashMap<u32, u32>,
    searched: HashSet<String>,
    last_opener: HashMap<String, Option<u32>>,
    searches: Vec<Search>,
    pending_opener: HashMap<u32, usize>,

    pawn_track: HashMap<u32, Vec<Pt>>,
    vehicle_track: HashMap<u32, Vec<Pt>>,
    seat_spans: Vec<SeatSpan>,
    seated: HashMap<(u32, usize), usize>, // (vehicle, seat) -> index in seat_spans
    orphans: Vec<Orphan>,
    orphan_channels: HashSet<u32>,
    tosses: Vec<(f32, Pt, u32)>,

    pawn_to_state: HashMap<u32, u32>,
    players: HashMap<u32, PlayerInfo>, // playerstate actor -> PlayerInfo
    player_order: Vec<u32>,

    prizes: Vec<Prize>,
    prize_channels: HashSet<u32>,

    handler_guids: HashMap<u32, String>,
    grants: Vec<Grant>,
    gizmo_items: HashMap<String, f32>,
}

impl Default for ChestScanReader {
    fn default() -> Self {
        Self::new()
    }
}

impl ChestScanReader {
    pub fn new() -> Self {
        let mut channels = Vec::with_capacity(DEFAULT_MAX_CHANNEL_SIZE);
        channels.resize_with(DEFAULT_MAX_CHANNEL_SIZE, || None);
        Self {
            info: ReplayInfo::default(),
            header: ReplayHeader::default(),
            is_partial_replay: false,
            current_time: 0.0,
            guid_cache: NetGuidCache::default(),
            channels,
            in_reliable: 0,
            in_packet_id: 0,
            partial_bunch: None,
            game_session_id: None,
            utc_time_started_match: None,
            recorder_id: None,
            current_playlist: None,
            builder_players: HashMap::new(),
            only_spectating_channels: HashSet::new(),
            channel_key: HashMap::new(),
            channel_label: HashMap::new(),
            channel_open_at: HashMap::new(),
            channel_searched: HashMap::new(),
            last_searcher: HashMap::new(),
            searched: HashSet::new(),
            last_opener: HashMap::new(),
            searches: Vec::new(),
            pending_opener: HashMap::new(),
            pawn_track: HashMap::new(),
            vehicle_track: HashMap::new(),
            seat_spans: Vec::new(),
            seated: HashMap::new(),
            orphans: Vec::new(),
            orphan_channels: HashSet::new(),
            tosses: Vec::new(),
            pawn_to_state: HashMap::new(),
            players: HashMap::new(),
            player_order: Vec::new(),
            prizes: Vec::new(),
            prize_channels: HashSet::new(),
            handler_guids: HashMap::new(),
            grants: Vec::new(),
            gizmo_items: HashMap::new(),
        }
    }

    pub fn read_replay_file(&mut self, path: &Path) -> Result<()> {
        let bytes = fs::read(path)?;
        self.read_replay_bytes(&bytes)
    }

    pub fn read_replay_bytes(&mut self, bytes: &[u8]) -> Result<()> {
        let mut archive = ByteReader::new(bytes);
        self.read_replay_info(&mut archive)?;
        self.read_replay_chunks(&mut archive)?;
        Ok(())
    }

    fn read_replay_info(&mut self, archive: &mut ByteReader) -> Result<()> {
        let magic = archive.read_u32();
        if magic != FILE_MAGIC {
            bail!("Invalid replay file magic: {:08X}", magic);
        }
        let file_version = archive.read_u32();
        archive.replay_version = file_version;

        if file_version >= 7 {
            // HISTORY_CUSTOM_VERSIONS
            let custom_version_count = archive.read_i32();
            if custom_version_count > 0 {
                archive.skip_bytes((custom_version_count as usize) * 20);
            }
        }

        let length_in_ms = archive.read_u32();
        let network_version = archive.read_u32();
        let changelist = archive.read_u32();
        let friendly_name = archive.read_fstring();
        let is_live = archive.read_u32_as_boolean();

        let mut timestamp = None;
        if file_version >= 3 {
            // HISTORY_RECORDED_TIMESTAMP
            let bin = archive.read_i64();
            // .NET DateTime.FromBinary: bottom 62 bits are 100ns ticks since 0001-01-01
            let ticks = bin & 0x3FFF_FFFF_FFFF_FFFF;
            // Unix epoch 1970-01-01 in .NET ticks is 621_355_968_000_000_000
            let unix_ticks = ticks - 621_355_968_000_000_000;
            let secs = unix_ticks.div_euclid(10_000_000);
            let nsecs = (unix_ticks.rem_euclid(10_000_000) * 100) as u32;
            timestamp = Utc.timestamp_opt(secs, nsecs).single();
        }

        let mut is_compressed = false;
        if file_version >= 2 {
            is_compressed = archive.read_u32_as_boolean();
        }

        let mut is_encrypted = false;
        let mut encryption_key = Vec::new();
        if file_version >= 6 {
            is_encrypted = archive.read_u32_as_boolean();
            let size = archive.read_u32() as usize;
            encryption_key = archive.read_bytes(size).to_vec();
        }

        if is_encrypted && encryption_key.is_empty() {
            bail!("Replay is marked encrypted but has no key");
        }

        self.info = ReplayInfo {
            file_version,
            length_in_ms,
            network_version,
            changelist,
            friendly_name,
            is_live,
            timestamp,
            is_compressed,
            is_encrypted,
            encryption_key,
        };
        Ok(())
    }

    fn read_replay_chunks(&mut self, archive: &mut ByteReader) -> Result<()> {
        while !archive.at_end() {
            if archive.len() - archive.pos < 8 {
                self.is_partial_replay = true;
                return Ok(());
            }

            let chunk_type = archive.read_u32();
            let chunk_size = archive.read_i32();
            let offset = archive.pos;

            if chunk_size > 0 && offset + (chunk_size as usize) > archive.len() {
                self.is_partial_replay = true;
                return Ok(());
            }

            if chunk_size <= 0 {
                archive.is_error = true;
                return Ok(());
            }

            let csize = chunk_size as usize;

            match chunk_type {
                0 => {
                    // Header
                    self.read_replay_header(archive)?;
                }
                1 => {
                    // ReplayData
                    match self.read_replay_data(archive, csize) {
                        Ok(()) => {}
                        Err(_e) if self.info.is_live => {
                            self.is_partial_replay = true;
                            return Ok(());
                        }
                        Err(e) => return Err(e),
                    }
                }
                2 => {
                    // Checkpoint -> skipped in normal scan (same as C#)
                }
                3 => {
                    // Event -> skipped (ChestScanReader only needs ReplayData and Header)
                }
                _ => {}
            }

            if archive.pos != offset + csize {
                archive.seek(offset + csize);
            }
        }
        Ok(())
    }

    fn read_replay_header(&mut self, archive: &mut ByteReader) -> Result<()> {
        let magic = archive.read_u32();
        if magic != NETWORK_MAGIC {
            bail!("Header.Magic != NETWORK_DEMO_MAGIC: {:08X}", magic);
        }
        let network_version = archive.read_u32();
        if network_version <= 5 {
            bail!("Header.Version < MIN_NETWORK_DEMO_VERSION");
        }
        if network_version >= 19 {
            let custom_version_count = archive.read_i32();
            if custom_version_count > 0 {
                archive.skip_bytes((custom_version_count as usize) * 20);
            }
        }
        let network_checksum = archive.read_u32();
        let engine_network_version = archive.read_u32();
        let game_network_protocol_version = archive.read_u32();
        let mut guid = String::new();
        if network_version >= 12 {
            guid = archive.read_guid_hex(16);
        }
        let mut major = 0u16;
        let mut minor = 0u16;
        let mut patch = 0u16;
        let changelist;
        let mut branch = String::new();
        if network_version >= 11 {
            major = archive.read_u16();
            minor = archive.read_u16();
            patch = archive.read_u16();
            changelist = archive.read_u32();
            branch = archive.read_fstring();
            archive.network_replay_version = NetworkReplayVersion {
                major,
                minor,
                patch,
                changelist,
                branch: branch.clone(),
            };
        } else {
            changelist = archive.read_u32();
        }

        if network_version >= 18 {
            let _ue4 = archive.read_u32();
            let _ue5 = archive.read_u32();
            let _pkg_lic = archive.read_u32();
        }

        let level_count = archive.read_u32();
        for _ in 0..level_count {
            let _name = archive.read_fstring();
            let _time = archive.read_u32();
        }

        let mut flags = 0u32;
        if network_version >= 9 {
            flags = archive.read_u32();
            archive.replay_header_flags = flags;
        }

        let game_specific_count = archive.read_u32();
        for _ in 0..game_specific_count {
            let _s = archive.read_fstring();
        }

        let mut platform = String::new();
        if network_version >= 17 {
            let _min_hz = archive.read_f32();
            let _max_hz = archive.read_f32();
            let _frame_limit = archive.read_f32();
            let _checkpoint_limit = archive.read_f32();
            platform = archive.read_fstring();
            let _build_cfg = archive.read_byte();
            let _build_target = archive.read_byte();
        }

        archive.engine_network_version = engine_network_version;
        archive.network_version = network_version;

        self.header = ReplayHeader {
            network_version,
            network_checksum,
            engine_network_version,
            game_network_protocol_version,
            guid,
            major,
            minor,
            patch,
            changelist,
            branch,
            flags,
            platform,
        };
        Ok(())
    }

    fn decrypt_buffer(&self, raw: &[u8]) -> Result<Vec<u8>> {
        if !self.info.is_encrypted {
            return Ok(raw.to_vec());
        }
        let key = &self.info.encryption_key;
        if key.len() != 32 {
            bail!("Invalid AES-256 key length: {}", key.len());
        }
        // Check if key is all zeros (unfinalized live replay)
        if key.iter().all(|&b| b == 0) {
            bail!("Replay encryption key is all zeros (live match in progress)");
        }
        let cipher = Aes256::new_from_slice(key).map_err(|_| anyhow::anyhow!("Invalid AES key"))?;
        let mut buf = raw.to_vec();
        let decrypted = cipher
            .decrypt_padded_mut::<Pkcs7>(&mut buf)
            .map_err(|_| anyhow::anyhow!("AES-ECB PKCS7 decryption failed"))?;
        let len = decrypted.len();
        buf.truncate(len);
        Ok(buf)
    }

    fn decompress_buffer(&self, decrypted: &[u8], ctx: &ByteReader) -> Result<Vec<u8>> {
        if !self.info.is_compressed {
            return Ok(decrypted.to_vec());
        }
        let mut ar = ctx.with_context(decrypted);
        let decompressed_size = ar.read_i32() as usize;
        let compressed_size = ar.read_i32() as usize;
        let compressed_buf = ar.read_bytes(compressed_size);
        if ar.is_error {
            bail!("Decompress header out of bounds");
        }
        decompress_replay_data(compressed_buf, decompressed_size)
    }

    fn read_replay_data(&mut self, archive: &mut ByteReader, fallback_chunk_size: usize) -> Result<()> {
        // ReplayVersionHistory is used as flags in C#: HasFlag(HISTORY_STREAM_CHUNK_TIMES = 4) and HasFlag(HISTORY_ENCRYPTION = 6 = 4|2)
        let length = if (archive.replay_version & 4) == 4 {
            let _start = archive.read_u32();
            let _end = archive.read_u32();
            archive.read_u32() as usize
        } else {
            fallback_chunk_size
        };

        if (archive.replay_version & 6) == 6 {
            let _memory_size_in_bytes = archive.read_i32();
        }

        let encrypted_slice = archive.read_bytes(length);
        if archive.is_error {
            bail!("ReplayData truncated");
        }
        let decrypted = self.decrypt_buffer(encrypted_slice)?;
        let decompressed = self.decompress_buffer(&decrypted, archive)?;
        let mut bin_archive = archive.with_context(&decompressed);

        while !bin_archive.at_end() {
            self.read_demo_frame_into_playback_packets(&mut bin_archive);
        }
        Ok(())
    }

    fn read_demo_frame_into_playback_packets(&mut self, archive: &mut ByteReader) {
        if archive.network_version >= 6 {
            let _current_level_index = archive.read_i32();
        }
        let time_seconds = archive.read_f32();
        self.current_time = time_seconds;

        if archive.network_version >= 10 {
            self.read_export_data(archive);
        }

        if archive.has_level_streaming_fixes() {
            let num = archive.read_int_packed();
            for _ in 0..num {
                let _ = archive.read_fstring();
            }
        } else {
            let num = archive.read_int_packed();
            for _ in 0..num {
                let _ = archive.read_fstring();
                let _ = archive.read_fstring();
                let _ = archive.read_ftransform();
            }
        }

        if archive.has_level_streaming_fixes() {
            let _external_offset = archive.read_u64();
        }

        self.read_external_data(archive);

        if archive.has_game_specific_frame_data() {
            let skip_ext = archive.read_u64();
            if skip_ext > 0 {
                archive.skip_bytes(skip_ext as usize);
            }
        }

        loop {
            if archive.has_level_streaming_fixes() {
                let _seen_level_index = archive.read_int_packed();
            }
            let buffer_size = archive.read_i32();
            if buffer_size <= 0 || buffer_size > 2048 || archive.is_error {
                break;
            }
            let packet = archive.read_bytes(buffer_size as usize);
            if archive.is_error || packet.is_empty() {
                break;
            }
            self.received_raw_packet(packet);
        }
    }

    fn read_export_data(&mut self, archive: &mut ByteReader) {
        self.read_net_field_exports(archive);
        self.read_net_export_guids(archive);
    }

    fn read_net_field_exports(&mut self, archive: &mut ByteReader) {
        let num_exports = archive.read_int_packed();
        for _ in 0..num_exports {
            let path_name_index = archive.read_int_packed();
            let is_exported = archive.read_int_packed() == 1;

            if is_exported {
                let path_name = archive.read_fstring();
                let num = archive.read_int_packed() as usize;
                if let Some(group) = self.guid_cache.export_group_map.get_mut(&path_name) {
                    if num > group.exports.len() {
                        group.exports.resize(num, None);
                    }
                } else {
                    let group = NetFieldExportGroup {
                        path_name: path_name.clone(),
                        path_name_index,
                        exports: vec![None; num],
                    };
                    self.guid_cache.add_to_export_group_map(path_name, group);
                }
            }

            let net_field = self.read_net_field_export(archive);
            if let (Some(group), Some(field)) = (
                self.guid_cache.get_group_from_index_mut(path_name_index),
                net_field,
            ) {
                let h = field.handle as usize;
                if h < group.exports.len() {
                    group.exports[h] = Some(field);
                }
            }
        }
    }

    fn read_net_field_export(&self, archive: &mut ByteReader) -> Option<NetFieldExport> {
        let is_exported = archive.read_boolean();
        if !is_exported {
            return None;
        }
        let handle = archive.read_int_packed();
        let compatible_checksum = archive.read_u32();
        let name = if archive.engine_network_version < 9 {
            let n = archive.read_fstring();
            let _t = archive.read_fstring();
            n
        } else if archive.engine_network_version < 10 {
            archive.read_fstring()
        } else {
            archive.read_fname()
        };
        Some(NetFieldExport {
            handle,
            compatible_checksum,
            name,
            incompatible: false,
        })
    }

    fn read_net_export_guids(&mut self, archive: &mut ByteReader) {
        let num_guids = archive.read_int_packed();
        for _ in 0..num_guids {
            let size = archive.read_i32();
            if size <= 0 {
                continue;
            }
            let slice = archive.read_bytes(size as usize);
            let mut reader = self.make_bit_reader(slice, slice.len() * 8);
            self.internal_load_object_bit(&mut reader, true, 0);
        }
    }

    fn read_external_data(&mut self, archive: &mut ByteReader) {
        loop {
            let num_bits = archive.read_int_packed();
            if num_bits == 0 || archive.is_error {
                return;
            }
            let net_guid = archive.read_int_packed();
            let byte_len = ((num_bits + 7) >> 3) as usize;
            let bytes = archive.read_bytes(byte_len).to_vec();
            self.guid_cache.external_data.insert(net_guid, bytes);
        }
    }

    fn make_bit_reader(&self, slice: &[u8], bit_count: usize) -> BitReader {
        let mut r = BitReader::from_slice(slice, bit_count);
        r.engine_network_version = self.header.engine_network_version;
        r.network_version = self.header.network_version;
        r.replay_header_flags = self.header.flags;
        r.replay_version = self.info.file_version;
        r.changelist = self.header.changelist;
        r.branch_is_41 = self.header.branch.contains("+Release-41.");
        r
    }

    fn internal_load_object_bit(
        &mut self,
        archive: &mut BitReader,
        is_exporting_net_guid_bunch: bool,
        recursion_count: usize,
    ) -> u32 {
        if recursion_count > 16 {
            return 0;
        }
        let net_guid = archive.read_int_packed();
        if net_guid == 0 {
            return 0;
        }
        if net_guid == 1 || is_exporting_net_guid_bunch {
            let flags = archive.read_byte();
            if (flags & 1) != 0 {
                // bHasPath
                let _outer_guid = self.internal_load_object_bit(archive, true, recursion_count + 1);
                let path_name = archive.read_fstring();
                if (flags & 4) != 0 {
                    // bHasNetworkChecksum
                    let _checksum = archive.read_u32();
                }
                if is_exporting_net_guid_bunch {
                    let stripped = remove_all_path_prefixes(&path_name).to_string();
                    self.guid_cache.guid_to_path_name.insert(net_guid, stripped);
                    self.on_net_guid_exported(net_guid, &path_name);
                }
                return net_guid;
            }
        }
        net_guid
    }

    fn receive_net_guid_bunch(&mut self, bit_archive: &mut BitReader) {
        let b_has_rep_layout_export = bit_archive.read_bit();
        if b_has_rep_layout_export {
            self.receive_net_field_exports_compat(bit_archive);
            return;
        }
        let num_guids = bit_archive.read_i32();
        if num_guids > MAX_GUID_COUNT || num_guids < 0 {
            return;
        }
        for _ in 0..num_guids {
            self.internal_load_object_bit(bit_archive, true, 0);
        }
    }

    fn receive_net_field_exports_compat(&mut self, bit_archive: &mut BitReader) {
        let num = bit_archive.read_u32();
        for _ in 0..num {
            let path_name_index = bit_archive.read_int_packed();
            if bit_archive.read_bit() {
                let path_name = bit_archive.read_fstring();
                let num_exports = bit_archive.read_u32() as usize;
                if !self.guid_cache.export_group_map.contains_key(&path_name) {
                    let group = NetFieldExportGroup {
                        path_name: path_name.clone(),
                        path_name_index,
                        exports: vec![None; num_exports],
                    };
                    self.guid_cache.add_to_export_group_map(path_name, group);
                }
            }
            let is_exported = bit_archive.read_bit();
            let net_field = if is_exported {
                let handle = bit_archive.read_int_packed();
                let compatible_checksum = bit_archive.read_u32();
                let name = if bit_archive.engine_network_version < 9 {
                    let n = bit_archive.read_fstring();
                    let _t = bit_archive.read_fstring();
                    n
                } else if bit_archive.engine_network_version < 10 {
                    bit_archive.read_fstring()
                } else {
                    bit_archive.read_fname()
                };
                Some(NetFieldExport {
                    handle,
                    compatible_checksum,
                    name,
                    incompatible: false,
                })
            } else {
                None
            };
            if let (Some(group), Some(field)) = (
                self.guid_cache.get_group_from_index_mut(path_name_index),
                net_field,
            ) {
                let h = field.handle as usize;
                if h < group.exports.len() {
                    group.exports[h] = Some(field);
                }
            }
        }
    }

    fn received_raw_packet(&mut self, packet: &[u8]) {
        let last_byte = *packet.last().unwrap_or(&0);
        if last_byte == 0 {
            return;
        }
        let lz = (last_byte as u32).leading_zeros() - 24;
        let bit_size = (packet.len() * 8) - 1 - (lz as usize);
        let mut packet_reader = self.make_bit_reader(packet, bit_size);
        self.received_packet(&mut packet_reader);
    }

    fn received_packet(&mut self, bit_reader: &mut BitReader) {
        self.in_packet_id += 1;
        let has_partial_custom_exports_final_bit = bit_reader.engine_network_version >= 36;

        while !bit_reader.at_end() {
            if bit_reader.engine_network_version < 8 {
                let _ = bit_reader.read_bit();
            }

            let mut bunch = DataBunch::default();
            let b_control = bit_reader.read_bit();
            bunch.packet_id = self.in_packet_id;
            bunch.b_open = b_control && bit_reader.read_bit();
            bunch.b_close = b_control && bit_reader.read_bit();

            if bit_reader.engine_network_version < 7 {
                bunch.b_dormant = bunch.b_close && bit_reader.read_bit();
                bunch.close_reason = if bunch.b_dormant { 1 } else { 0 };
            } else {
                bunch.close_reason = if bunch.b_close {
                    bit_reader.read_serialized_int(15)
                } else {
                    0
                };
                bunch.b_dormant = bunch.close_reason == 1;
            }

            bunch.b_is_replication_paused = bit_reader.read_bit();
            bunch.b_reliable = bit_reader.read_bit();

            if bit_reader.engine_network_version < 3 {
                bunch.ch_index = bit_reader.read_serialized_int(OLD_MAX_ACTOR_CHANNELS);
            } else {
                bunch.ch_index = bit_reader.read_int_packed();
            }

            bunch.b_has_package_map_exports = bit_reader.read_bit();
            bunch.b_has_must_be_mapped_guids = bit_reader.read_bit();
            bunch.b_partial = bit_reader.read_bit();

            if bunch.b_reliable {
                bunch.ch_sequence = self.in_reliable + 1;
            } else if bunch.b_partial {
                bunch.ch_sequence = self.in_packet_id;
            } else {
                bunch.ch_sequence = 0;
            }

            bunch.b_partial_initial = bunch.b_partial && bit_reader.read_bit();
            bunch.b_has_partial_custom_exports_final_bit =
                bunch.b_partial && has_partial_custom_exports_final_bit && bit_reader.read_bit();
            bunch.b_partial_final = bunch.b_partial && bit_reader.read_bit();

            if bit_reader.engine_network_version < 6 {
                let _ = bit_reader.read_serialized_int(8);
            } else if bunch.b_reliable || bunch.b_open {
                let _ = bit_reader.read_fname();
            }

            if (bunch.ch_index as usize) >= self.channels.len() {
                break;
            }
            let has_channel = self.channels[bunch.ch_index as usize].is_some();

            let bunch_data_bits = bit_reader.read_serialized_int(MAX_PACKET_SIZE_IN_BITS) as usize;

            if bunch.b_partial {
                let slice = bit_reader.read_bits_to_vec(bunch_data_bits);
                bunch.archive = self.make_bit_reader(&slice, bunch_data_bits);
            } else {
                bit_reader.set_temp_end(bunch_data_bits, EndIndex::Bunch);
            }

            if bit_reader.is_error {
                break;
            }

            if bunch.b_has_package_map_exports {
                if bunch.b_partial {
                    let mut arch = std::mem::take(&mut bunch.archive);
                    self.receive_net_guid_bunch(&mut arch);
                    bunch.archive = arch;
                } else {
                    self.receive_net_guid_bunch(bit_reader);
                }
            }

            if !has_channel {
                self.channels[bunch.ch_index as usize] = Some(UChannel::new(bunch.ch_index));
            }

            if bunch.b_partial {
                self.received_next_bunch_partial(bunch);
            } else {
                // Non-partial: operates directly on bit_reader with BUNCH temp_end
                if bunch.b_reliable {
                    self.in_reliable = bunch.ch_sequence;
                }
                self.received_sequenced_bunch(&bunch, bit_reader);
                bit_reader.restore_temp_end(EndIndex::Bunch);
            }
        }
    }

    fn received_next_bunch_partial(&mut self, bunch: DataBunch) {
        if bunch.b_reliable {
            self.in_reliable = bunch.ch_sequence;
        }
        if bunch.b_partial_initial {
            if let Some(ref existing) = self.partial_bunch {
                if !existing.b_partial_final && existing.b_reliable {
                    return;
                }
                self.partial_bunch = None;
            }
            let bits_left = bunch.archive.get_bits_left();
            if !bunch.b_has_package_map_exports && bits_left > 0 && (bits_left % 8 != 0) {
                return;
            }
            self.partial_bunch = Some(bunch);
            return;
        }

        let mut completed: Option<DataBunch> = None;
        if let Some(ref mut partial) = self.partial_bunch {
            let reliable_matches = bunch.ch_sequence == partial.ch_sequence + 1;
            let unreliable_matches = reliable_matches || (bunch.ch_sequence == partial.ch_sequence);
            let seq_matches = if partial.b_reliable {
                reliable_matches
            } else {
                unreliable_matches
            };

            if !partial.b_partial_final && seq_matches && partial.b_reliable == bunch.b_reliable {
                let mut incoming_arch = bunch.archive;
                let bits_left = incoming_arch.get_bits_left();
                if !bunch.b_has_package_map_exports && bits_left > 0 {
                    let bytes = incoming_arch.read_bits_to_vec(bits_left);
                    partial.archive.append_data_from_checked(&bytes, bits_left);
                }
                if !bunch.b_has_package_map_exports && !bunch.b_partial_final && (bits_left % 8 != 0)
                {
                    return;
                }
                partial.ch_sequence = bunch.ch_sequence;
                if bunch.b_partial_final {
                    if bunch.b_has_package_map_exports {
                        return;
                    }
                    partial.b_partial_final = true;
                    partial.b_close = bunch.b_close;
                    partial.b_dormant = bunch.b_dormant;
                    partial.close_reason = bunch.close_reason;
                    partial.b_is_replication_paused = bunch.b_is_replication_paused;
                    partial.b_has_must_be_mapped_guids = bunch.b_has_must_be_mapped_guids;
                    completed = self.partial_bunch.take();
                }
            }
        }

        if let Some(mut final_bunch) = completed {
            let mut arch = std::mem::take(&mut final_bunch.archive);
            self.received_sequenced_bunch(&final_bunch, &mut arch);
        }
    }

    fn received_sequenced_bunch(&mut self, bunch: &DataBunch, archive: &mut BitReader) {
        if bunch.b_has_must_be_mapped_guids {
            let num = archive.read_u16();
            for _ in 0..num {
                let _ = archive.read_int_packed();
            }
        }
        self.process_bunch(bunch, archive);

        if bunch.b_close {
            self.channels[bunch.ch_index as usize] = None;
        }
    }

    fn conditionally_serialize_quantized_vector(
        archive: &mut BitReader,
        def_x: f64,
        def_y: f64,
        def_z: f64,
    ) -> FVector {
        if archive.read_bit() {
            let should_quantize = archive.engine_network_version < 13 || archive.read_bit();
            if should_quantize {
                archive.read_packed_vector(10, 24)
            } else {
                archive.read_fvector()
            }
        } else {
            FVector {
                x: def_x,
                y: def_y,
                z: def_z,
            }
        }
    }

    fn process_bunch(&mut self, bunch: &DataBunch, archive: &mut BitReader) {
        let ch_idx = bunch.ch_index as usize;
        let needs_actor = self.channels[ch_idx]
            .as_ref()
            .is_some_and(|c| c.actor.is_none());

        if needs_actor {
            if !bunch.b_open {
                return;
            }
            let actor_net_guid = self.internal_load_object_bit(archive, false, 0);
            let is_dynamic = actor_net_guid > 0 && (actor_net_guid & 1) != 1;
            if archive.at_end() && is_dynamic {
                return;
            }

            let mut in_actor = Actor {
                actor_net_guid,
                archetype: None,
                level: None,
                location: None,
                rotation: FRotator::default(),
                scale: FVector {
                    x: 1.0,
                    y: 1.0,
                    z: 1.0,
                },
                velocity: FVector::default(),
            };

            if is_dynamic {
                let arch = self.internal_load_object_bit(archive, false, 0);
                in_actor.archetype = if arch > 0 { Some(arch) } else { None };
                if archive.engine_network_version >= 5 {
                    let lvl = self.internal_load_object_bit(archive, false, 0);
                    in_actor.level = if lvl > 0 { Some(lvl) } else { None };
                }
                in_actor.location =
                    Some(Self::conditionally_serialize_quantized_vector(archive, 0.0, 0.0, 0.0));
                if archive.read_bit() {
                    in_actor.rotation = archive.read_rotation_short();
                }
                in_actor.scale =
                    Self::conditionally_serialize_quantized_vector(archive, 1.0, 1.0, 1.0);
                in_actor.velocity =
                    Self::conditionally_serialize_quantized_vector(archive, 0.0, 0.0, 0.0);
            }

            let archetype_id = in_actor.archetype;
            if let Some(ref mut ch) = self.channels[ch_idx] {
                ch.actor = Some(in_actor);
            }
            self.on_channel_opened(bunch.ch_index, actor_net_guid);

            if let Some(arch_guid) = archetype_id {
                if let Some(path) = self.guid_cache.guid_to_path_name.get(&arch_guid) {
                    if path == "BP_ReplayPC_Athena_C" {
                        let _net_player_index = archive.read_byte();
                    }
                }
            }
        }

        while !archive.at_end() {
            let (rep_object, b_object_deleted, b_has_rep_layout, payload) =
                self.read_content_block_payload(bunch.ch_index, archive);

            if archive.is_error {
                break;
            }
            let Some(payload_bits) = payload else {
                continue;
            };

            archive.set_temp_end(payload_bits as usize, EndIndex::ContentBlockPayload);

            if !b_object_deleted && !archive.is_error {
                if let Some(rep_obj) = rep_object {
                    if !archive.at_end() {
                        let _ = self.received_replicator_bunch(
                            bunch.ch_index,
                            archive,
                            rep_obj,
                            b_has_rep_layout,
                        );
                    }
                }
            }

            archive.restore_temp_end(EndIndex::ContentBlockPayload);
        }
    }

    fn read_content_block_payload(
        &mut self,
        ch_index: u32,
        archive: &mut BitReader,
    ) -> (Option<u32>, bool, bool, Option<u32>) {
        let (rep_object, b_out_has_rep_layout, b_object_deleted) =
            self.read_content_block_header(ch_index, archive);
        if b_object_deleted {
            return (rep_object, true, b_out_has_rep_layout, None);
        }
        let payload = archive.read_int_packed();
        (rep_object, false, b_out_has_rep_layout, Some(payload))
    }

    fn read_content_block_header(
        &mut self,
        ch_index: u32,
        archive: &mut BitReader,
    ) -> (Option<u32>, bool, bool) {
        let b_out_has_rep_layout = archive.read_bit();
        let b_is_actor = archive.read_bit();
        if b_is_actor {
            let ch = self.channels[ch_index as usize].as_ref();
            let obj = ch.and_then(|c| c.archetype_id().or_else(|| c.actor_id()));
            return (obj, b_out_has_rep_layout, false);
        }

        let net_guid = self.internal_load_object_bit(archive, false, 0);
        let b_stably_named = archive.read_bit();
        if b_stably_named {
            return (Some(net_guid), b_out_has_rep_layout, false);
        }

        let mut b_delete_sub_object = false;
        let mut b_serialize_class = true;
        if archive.engine_network_version >= 30 {
            let b_is_destroy_message = archive.read_bit();
            if b_is_destroy_message {
                b_delete_sub_object = true;
                b_serialize_class = false;
                let _destroy_flags = archive.read_byte();
            }
        }

        let mut class_net_guid = 0u32;
        if b_serialize_class {
            class_net_guid = self.internal_load_object_bit(archive, false, 0);
            b_delete_sub_object = class_net_guid == 0;
        }

        if b_delete_sub_object {
            return (None, b_out_has_rep_layout, true);
        }

        if archive.engine_network_version >= 18 {
            let b_actor_is_outer = archive.at_end() || archive.read_bit();
            if !b_actor_is_outer {
                let _ = self.internal_load_object_bit(archive, false, 0);
            }
        }

        (Some(class_net_guid), b_out_has_rep_layout, false)
    }

    fn received_replicator_bunch(
        &mut self,
        ch_index: u32,
        archive: &mut BitReader,
        rep_object: u32,
        b_has_rep_layout: bool,
    ) -> bool {
        let Some(group_name) = self.guid_cache.get_group_by_guid(Some(rep_object)) else {
            return true;
        };

        let known = resolve_known_group(&group_name);
        // RawProbe hook for FortAbilitySystemComponent
        if known.is_none() && group_name.contains("FortAbilitySystemComponent") {
            return self.probe_replicator_bunch(ch_index, archive, &group_name, b_has_rep_layout);
        }

        if b_has_rep_layout {
            if !self.receive_properties(archive, &group_name, known, ch_index, true, false) {
                return false;
            }
            self.receive_external_data(&group_name, ch_index);
        }

        if archive.at_end() {
            return true;
        }

        let use_full_name = archive.engine_network_version >= 15;
        let Some(class_net_cache_name) =
            self.guid_cache.get_class_net_cache_name(&group_name, use_full_name)
        else {
            return false;
        };
        let cache_short_name = self
            .guid_cache
            .export_group_map
            .get(&class_net_cache_name)
            .map(|g| g.path_name.clone())
            .unwrap_or_else(|| class_net_cache_name.clone());

        while let Some((field_name, field_incompatible, payload_bits)) =
            self.read_field_header_and_payload(archive, &class_net_cache_name)
        {
            archive.set_temp_end(payload_bits as usize, EndIndex::FieldHeaderPayload);

            if field_name.is_none() {
                self.probe_field(ch_index, &cache_short_name, "?", archive);
            } else if !field_incompatible && !archive.is_error && !archive.at_end() {
                let fname = field_name.as_deref().unwrap_or("");
                if cache_short_name == "Athena_GameState_C_ClassNetCache" {
                    if fname == "CurrentPlaylistInfo" {
                        // CustomStruct PlaylistInfo
                        let num_bits = archive.get_bits_left();
                        let mut cmd_reader = BitReader::new();
                        cmd_reader.copy_versions_from(archive);
                        cmd_reader.fill_bits_from(archive, num_bits);
                        if cmd_reader.engine_network_version >= 11 {
                            let _ = cmd_reader.read_bit();
                        }
                        let _ = cmd_reader.read_bit();
                        let id = cmd_reader.read_int_packed();
                        cmd_reader.skip_bits(31);
                        if let Some(name) = self.guid_cache.guid_to_path_name.get(&id) {
                            if self.current_playlist.is_none() {
                                self.current_playlist = Some(name.clone());
                            }
                        }
                    } else if fname == "ActiveGameplayModifiers" {
                        let _ = self.receive_custom_delta_property(
                            archive,
                            "/Script/FortniteGame.ActiveGameplayModifier",
                            ch_index,
                            false,
                        );
                    }
                } else {
                    self.probe_field(ch_index, &cache_short_name, fname, archive);
                }
            }

            archive.restore_temp_end(EndIndex::FieldHeaderPayload);
        }

        true
    }

    fn read_field_header_and_payload(
        &self,
        archive: &mut BitReader,
        class_net_cache_name: &str,
    ) -> Option<(Option<String>, bool, u32)> {
        if archive.at_end() {
            return None;
        }
        let group = self.guid_cache.export_group_map.get(class_net_cache_name)?;
        let max_val = (group.exports.len() as u32).max(2);
        let handle = archive.read_serialized_int(max_val) as usize;
        if archive.is_error {
            return None;
        }
        let out_field = group.exports.get(handle).and_then(|e| e.as_ref());
        let field_name = out_field.map(|f| f.name.clone());
        let incompatible = out_field.is_some_and(|f| f.incompatible);

        let payload = archive.read_int_packed();
        if archive.is_error || !archive.can_read(payload as usize) {
            return None;
        }
        Some((field_name, incompatible, payload))
    }

    fn probe_field(
        &mut self,
        ch_index: u32,
        group: &str,
        field: &str,
        archive: &mut BitReader,
    ) {
        if group.contains("FortAbilitySystemComponent")
            && group.ends_with("_ClassNetCache")
            && (field == "?" || field == "ActivatableAbilities")
            && !self.handler_guids.is_empty()
        {
            let bits = archive.get_bits_left();
            if bits > 0 {
                let data = archive.read_bits_to_vec(bits);
                self.read_ability_grants(ch_index, &data, bits);
            }
        }
    }

    fn probe_replicator_bunch(
        &mut self,
        ch_index: u32,
        archive: &mut BitReader,
        group_name: &str,
        b_has_rep_layout: bool,
    ) -> bool {
        if b_has_rep_layout {
            let _checksum = archive.read_bit();
            loop {
                let mut handle = archive.read_int_packed();
                if handle == 0 || archive.is_error {
                    break;
                }
                handle -= 1;
                let num_bits = archive.read_int_packed() as usize;
                if archive.is_error || num_bits > archive.get_bits_left() {
                    return false;
                }
                if num_bits == 0 {
                    continue;
                }
                let _ = handle;
                archive.skip_bits(num_bits);
            }
        }
        if archive.at_end() {
            return true;
        }
        let use_full_name = archive.engine_network_version >= 15;
        let Some(class_net_cache_name) =
            self.guid_cache.get_class_net_cache_name(group_name, use_full_name)
        else {
            return false;
        };

        while let Some((field_name, _, payload_bits)) =
            self.read_field_header_and_payload(archive, &class_net_cache_name)
        {
            archive.set_temp_end(payload_bits as usize, EndIndex::FieldHeaderPayload);
            let bits = archive.get_bits_left();
            if bits > 0 && !archive.is_error {
                let fname = field_name.as_deref().unwrap_or("?");
                self.probe_field(ch_index, &class_net_cache_name, fname, archive);
            }
            archive.restore_temp_end(EndIndex::FieldHeaderPayload);
        }
        true
    }

    fn receive_external_data(&mut self, group_name: &str, ch_index: u32) {
        let Some(ch) = self.channels[ch_index as usize].as_ref() else {
            return;
        };
        if ch.ignore_groups.contains(group_name) {
            return;
        }
        if let Some(actor_guid) = ch.actor_id() {
            if let Some(ext_bytes) = self.guid_cache.external_data.remove(&actor_guid) {
                // PlayerNameData decoding
                let mut ar = ByteReader::new(&ext_bytes);
                let _handle = ar.read_byte();
                let _unknown1 = ar.read_byte();
                let is_player = ar.read_boolean();
                let encoded_name = ar.read_fstring();
                let decoded_name = if is_player {
                    let chars: Vec<char> = encoded_name.chars().collect();
                    let len = chars.len();
                    let mut out = String::with_capacity(len);
                    for (i, &c) in chars.iter().enumerate() {
                        let shift = ((len % 4) * 3 % 8 + 1 + i) * 3 % 8;
                        let code = (c as u32).wrapping_add(shift as u32);
                        if let Some(dc) = char::from_u32(code) {
                            out.push(dc);
                        }
                    }
                    out
                } else {
                    encoded_name
                };
                if let Some(bp) = self.builder_players.get_mut(&ch_index) {
                    bp.player_name = Some(decoded_name);
                }
            }
        }
    }

    fn receive_custom_delta_property(
        &mut self,
        reader: &mut BitReader,
        group_path: &str,
        ch_index: u32,
        enable_property_checksum: bool,
    ) -> bool {
        let Some(known) = resolve_known_group(group_path) else {
            return true;
        };
        if !self.guid_cache.export_group_map.contains_key(group_path) {
            return true;
        }
        if reader.engine_network_version >= 11 {
            let _ = reader.read_bit();
        }
        let _array_rep_key = reader.read_i32();
        let _base_rep_key = reader.read_i32();
        let num_deletes = reader.read_i32();
        let num_changed = reader.read_i32();
        if reader.is_error || num_deletes < 0 || num_changed < 0 {
            return false;
        }
        for _ in 0..num_deletes {
            let _elem_idx = reader.read_i32();
        }
        for _ in 0..num_changed {
            let _elem_idx = reader.read_i32();
            self.receive_properties(
                reader,
                group_path,
                Some(known),
                ch_index,
                !enable_property_checksum,
                true,
            );
        }
        true
    }

    fn receive_properties(
        &mut self,
        archive: &mut BitReader,
        group_name: &str,
        known_opt: Option<KnownGroup>,
        ch_index: u32,
        enable_property_checksum: bool,
        net_delta_update: bool,
    ) -> bool {
        let Some(ch) = self.channels[ch_index as usize].as_ref() else {
            return false;
        };
        if ch.ignore_groups.contains(group_name) {
            return false;
        }
        let Some(known) = known_opt else {
            if let Some(ch_mut) = self.channels[ch_index as usize].as_mut() {
                ch_mut.ignore_groups.insert(group_name.to_string());
            }
            return false;
        };

        if enable_property_checksum {
            let _do_checksum = archive.read_bit();
        }

        let mut container = BaseContainerData::default();
        let mut prize_chest = TreasureHuntChestData::default();
        let mut pawn = PlayerPawnData::default();
        let mut vehicle = GenericVehicleData::default();
        let mut seats = SeatComponentData::default();
        let mut pickup = FortPickupData::default();
        let mut player_state = FortPlayerStateData::default();
        let mut game_state = GameStateData::default();

        let mut has_data = false;
        let mut cmd_reader = BitReader::new();
        cmd_reader.copy_versions_from(archive);

        loop {
            let mut handle = archive.read_int_packed();
            if handle == 0 {
                break;
            }
            handle -= 1;

            let (export_name, is_incompat, valid_idx) = {
                let Some(group) = self.guid_cache.export_group_map.get(group_name) else {
                    return false;
                };
                if !group.is_valid_index(handle) {
                    return false;
                }
                match &group.exports[handle as usize] {
                    Some(exp) => (Some(exp.name.clone()), exp.incompatible, true),
                    None => (None, false, true),
                }
            };
            if !valid_idx {
                return false;
            }

            let num_bits = archive.read_int_packed() as usize;
            if num_bits == 0 {
                continue;
            }
            let Some(name) = export_name else {
                archive.skip_bits(num_bits);
                continue;
            };
            if is_incompat {
                archive.skip_bits(num_bits);
                continue;
            }

            has_data = true;
            cmd_reader.fill_bits_from(archive, num_bits);

            let parsed_ok = self.parse_export_field(
                known,
                group_name,
                &name,
                &mut cmd_reader,
                &mut container,
                &mut prize_chest,
                &mut pawn,
                &mut vehicle,
                &mut seats,
                &mut pickup,
                &mut player_state,
                &mut game_state,
            );

            if !parsed_ok || cmd_reader.is_error || !cmd_reader.at_end() {
                if let Some(group) = self.guid_cache.export_group_map.get_mut(group_name) {
                    if let Some(Some(exp)) = group.exports.get_mut(handle as usize) {
                        exp.incompatible = true;
                    }
                }
            }
        }

        if !net_delta_update && has_data {
            self.on_export_read(
                ch_index,
                known,
                container,
                prize_chest,
                pawn,
                vehicle,
                seats,
                pickup,
                player_state,
                game_state,
            );
        }

        true
    }

    #[allow(clippy::too_many_arguments)]
    fn parse_export_field(
        &self,
        known: KnownGroup,
        group_name: &str,
        field_name: &str,
        r: &mut BitReader,
        container: &mut BaseContainerData,
        prize_chest: &mut TreasureHuntChestData,
        pawn: &mut PlayerPawnData,
        vehicle: &mut GenericVehicleData,
        seats: &mut SeatComponentData,
        pickup: &mut FortPickupData,
        player_state: &mut FortPlayerStateData,
        game_state: &mut GameStateData,
    ) -> bool {
        let default_rot_quant = if r.changelist >= 54618515 || r.branch_is_41 {
            RotatorQuantization::ShortComponents
        } else {
            RotatorQuantization::ByteComponents
        };

        match known {
            KnownGroup::GenericContainer => match field_name {
                "bHidden" | "RemoteRole" | "Role" | "ForceMetadataRelevant" | "StaticMesh"
                | "AltMeshIdx" | "BuildTime" | "RepairTime" | "SearchedMesh" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "bDestroyed" => {
                    container.b_destroyed = r.read_bit();
                    true
                }
                "bInstantDeath" => {
                    container.b_instant_death = r.read_bit();
                    true
                }
                "WeaponData" => {
                    let _ = r.read_int_packed();
                    true
                }
                "Health" | "MaxHealth" => {
                    let _ = r.read_u16();
                    true
                }
                "ReplicatedLootTier" | "ChosenRandomUpgrade" => {
                    let _ = r.read_i32();
                    true
                }
                "SearchingPawn" => {
                    container.searching_pawn = Some(r.read_int_packed());
                    true
                }
                "bAlreadySearched" => {
                    container.b_already_searched = Some(r.read_bit());
                    true
                }
                "BounceNormal" => {
                    let _ = r.read_fvector();
                    true
                }
                "SearchAnimationCount" => {
                    container.search_animation_count = Some(r.read_u32());
                    true
                }
                "bMirrored" | "bIsInitiallyBuilding" | "bForceReplayRollback" => {
                    let _ = r.read_bit();
                    true
                }
                "ReplicatedDrawScale3D" => {
                    let _ = r.read_packed_vector(100, 30);
                    true
                }
                _ => false,
            },

            KnownGroup::TreasureHuntChest => match field_name {
                "Instigator" => {
                    prize_chest.instigator = Some(r.read_int_packed());
                    true
                }
                "OwnerPersistentID" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "SourceContainer" => {
                    prize_chest.source_container = Some(r.read_int_packed());
                    true
                }
                "OpeningPawn" => {
                    prize_chest.opening_pawn = Some(r.read_int_packed());
                    true
                }
                "bSuccess" => {
                    prize_chest.b_success = Some(r.read_bit());
                    true
                }
                "PrizeTemplateId" => {
                    let bits = r.get_bits_left();
                    prize_chest.prize_template_bytes = Some(r.read_bits_to_vec(bits));
                    true
                }
                "Tier" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "TierData" => {
                    let _ = r.read_int_packed();
                    true
                }
                _ => false,
            },

            KnownGroup::GenericVehicle => match field_name {
                "ReplicatedMovement" => {
                    vehicle.replicated_movement = Some(r.serialize_rep_movement(
                        VectorQuantization::RoundTwoDecimals,
                        RotatorQuantization::ShortComponents,
                        VectorQuantization::RoundTwoDecimals,
                    ));
                    true
                }
                _ => false,
            },

            KnownGroup::SeatComponent => match field_name {
                "player" => {
                    seats.player = Some(r.read_int_packed());
                    true
                }
                "PlayerSlots" => {
                    seats.player_slots = self.read_seat_slots_array(group_name, r);
                    seats.player_slots.is_some()
                }
                _ => false,
            },

            KnownGroup::FortPickup => match field_name {
                "bReplicateMovement"
                | "bRandomRotation"
                | "bUpdateStatsOnCollection"
                | "bIsDirty"
                | "bCombinePickupsWhenTossCompletes"
                | "bPickedUp"
                | "bServerStoppedSimulation" => {
                    let _ = r.read_bit();
                    true
                }
                "bTossedFromContainer" => {
                    pickup.b_tossed_from_container = Some(r.read_bit());
                    true
                }
                "RemoteRole" | "AttachParent" | "Role" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "ReplicatedMovement" => {
                    let _ = r.serialize_rep_movement(
                        VectorQuantization::RoundTwoDecimals,
                        default_rot_quant,
                        VectorQuantization::RoundWholeNumber,
                    );
                    true
                }
                "LocationOffset" | "RelativeScale3D" => {
                    let _ = r.read_packed_vector(100, 30);
                    true
                }
                "RotationOffset" => {
                    let _ = r.read_rotation_short();
                    true
                }
                "AttachComponent" | "CombineTarget" | "PickupTarget" | "ItemOwner"
                | "ItemDefinition" | "PawnWhoDroppedPickup" => {
                    let _ = r.read_int_packed();
                    true
                }
                "Owner" => {
                    pickup.owner = Some(r.read_int_packed());
                    true
                }
                "Count" | "Level" | "LoadedAmmo" | "OptionalOwnerID" => {
                    let _ = r.read_i32();
                    true
                }
                "Durability" | "FlyTime" => {
                    let _ = r.read_f32();
                    true
                }
                "A" | "B" | "C" | "D" => {
                    let _ = r.read_u32();
                    true
                }
                "StateValues" => {
                    // FortItemEntryStateValue has minimalParseMode: ParseMode.Ignore -> ReadArrayField returns null -> field marked incompatible!
                    false
                }
                "GenericAttributeValues" => self.skip_primitive_array(r, 32),
                "LootInitialPosition" | "LootFinalPosition" | "FinalTossRestLocation" => {
                    let _ = r.read_packed_vector(10, 24);
                    true
                }
                "StartDirection" => {
                    let _ = r.serialize_vector_normal();
                    true
                }
                "TossState" => {
                    let _ = r.read_bits_to_u32(r.get_bits_left());
                    true
                }
                "ServerImpactSoundFlash" => {
                    let _ = r.read_byte();
                    true
                }
                "OrderIndex" => {
                    let _ = r.read_u16();
                    true
                }
                _ => false,
            },

            KnownGroup::FortPlayerState => match field_name {
                "RemoteRole" | "Owner" | "Role" | "Ping" | "PlayerNamePrivate"
                | "WasReplicatedFlags" | "WasPartReplicatedFlags" | "WorldLocation"
                | "SimulatedAttributes" | "bHidden" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "Instigator" | "HeroType" | "Parts" | "PlayerTeamPrivate" | "FinisherOrDowner" => {
                    let _ = r.read_int_packed();
                    true
                }
                "Score"
                | "RequiredVariantPartFlags"
                | "TeamKillScore"
                | "TeamScorePlacement"
                | "TeamScore"
                | "KillScore"
                | "SeasonLevelUIDisplay"
                | "RebootCounter" => {
                    let _ = r.read_u32();
                    true
                }
                "PlayerID" => {
                    player_state.player_id_i32 = Some(r.read_i32());
                    true
                }
                "PlayerId" => {
                    player_state.player_id_u32 = Some(r.read_u32());
                    true
                }
                "bIsABot" => {
                    player_state.b_is_a_bot = Some(r.read_bit());
                    true
                }
                "bOnlySpectator" => {
                    player_state.b_only_spectator = Some(r.read_bit());
                    true
                }
                "bIsSpectator"
                | "bIsGameSessionOwner"
                | "bHasFinishedLoading"
                | "bHasStartedPlaying"
                | "bHasEverSkydivedFromBus"
                | "bHasEverSkydivedFromBusAndLanded"
                | "bInAircraft"
                | "bThankedBusDriver"
                | "bDidNotThankBusDriver"
                | "bUsingStreamerMode"
                | "bInitialized"
                | "bUsingAnonymousMode"
                | "bIsDisconnected"
                | "bDBNO"
                | "bResurrectionChipAvailable"
                | "bResurrectingNow"
                | "bHoldsRebootVanLock"
                | "bIsAnAthenaGameParticipant" => {
                    let _ = r.read_bit();
                    true
                }
                "StartTime"
                | "Place"
                | "SquadListUpdateValue"
                | "Level"
                | "DeathCircumstance"
                | "NumRejoins" => {
                    let _ = r.read_i32();
                    true
                }
                "UniqueId" | "UniqueID" | "PartyOwnerUniqueId" | "PlatformUniqueNetId"
                | "BotUniqueId" => {
                    let _ = r.serialize_property_net_id();
                    true
                }
                "PlayerRole"
                | "CharacterGender"
                | "CharacterBodyType"
                | "ReplicatedTeamMemberState"
                | "DeathCause"
                | "KickedFromSessionReason" => {
                    let _ = r.read_bits_to_u32(r.get_bits_left());
                    true
                }
                "TeamIndex" => {
                    player_state.team_index = Some(r.read_bits_to_u32(r.get_bits_left()) as i32);
                    true
                }
                "WorldPlayerId" => {
                    let _ = r.read_i16();
                    true
                }
                "Platform" | "IconId" | "ColorId" => {
                    let _ = r.read_fstring();
                    true
                }
                "VariantRequiredCharacterParts" => self.skip_packed_int_array(r),
                "SquadId" => {
                    let _ = r.read_byte();
                    true
                }
                "StreamerModeName" | "PlayerNameCustomOverride" => {
                    let _flags = r.read_i32();
                    let history_type = r.read_byte();
                    if history_type == 0 {
                        let _ns = r.read_fstring();
                        let _key = r.read_fstring();
                        let _txt = r.read_fstring();
                    }
                    true
                }
                "MapIndicatorPos" => {
                    let _ = r.read_f32();
                    let _ = r.read_f32();
                    true
                }
                "Distance"
                | "ResurrectionExpirationTime"
                | "ResurrectionExpirationLength" => {
                    let _ = r.read_f32();
                    true
                }
                "DeathTags" | "VictimTags" | "FinisherOrDownerTags" => {
                    self.skip_gameplay_tag_container(r);
                    true
                }
                "DeathLocation" => {
                    let _ = r.read_fvector();
                    true
                }
                _ => false,
            },

            KnownGroup::PlayerPawn => match field_name {
                "Owner" | "AttachComponent" | "Instigator" | "Controller" | "MovementBase"
                | "CurrentWeapon" | "LastReplicatedEmoteExecuted" | "Vehicle" | "PawnMontage"
                | "FootstepBankOverride" | "GroupEmoteLookTarget" | "Zipline" | "PetState"
                | "ParachuteAttachment" | "SkyDiveContrail" | "Glider" | "Pickaxe"
                | "Character" | "DBNOHoister" | "Backpack" | "LoadingScreen" | "MusicPack"
                | "PetSkin" | "ReplicatedWaterBody" => {
                    let _ = r.read_int_packed();
                    true
                }
                "PlayerState" => {
                    pawn.player_state = Some(r.read_int_packed());
                    true
                }
                "ReplicatedMovement" => {
                    pawn.replicated_movement = Some(r.serialize_rep_movement(
                        VectorQuantization::RoundTwoDecimals,
                        default_rot_quant,
                        VectorQuantization::RoundWholeNumber,
                    ));
                    true
                }
                "bHidden"
                | "bReplicateMovement"
                | "bTearOff"
                | "RemoteRole"
                | "AttachParent"
                | "Role"
                | "Acceleration"
                | "FeedbackAudioComponent"
                | "VocalChords"
                | "JumpFlashCountPacked"
                | "LandingFlashCountPacked"
                | "StasisMode"
                | "MovementDir"
                | "AuthoritativeValue"
                | "AuthoritativeRootMotion"
                | "SocketOffset"
                | "AnimMontage"
                | "AnimRootMotionTranslationScale"
                | "ReplayRepAnimMontageInfo"
                | "RepAnimMontageInfo"
                | "CharacterVariantChannels"
                | "DBNOCarryEvent"
                | "ItemSpecialActorID"
                | "NextSectionID"
                | "FastReplicationMinimalReplicationTags"
                | "bIsCreativeGhostModeActivated"
                | "PlayRespawnFXOnSpawn" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "bCanBeDamaged"
                | "bServerHasBaseComponent"
                | "bRelativeRotation"
                | "bServerHasVelocity"
                | "bIsCrouched"
                | "bProxyIsJumpForceApplied"
                | "bIsActive"
                | "bIgnoreNextFallingDamage"
                | "bIsDying"
                | "bIsInvulnerable"
                | "bMovingEmote"
                | "bWeaponActivated"
                | "bIsDBNO"
                | "bWasDBNOOnDeath"
                | "bWeaponHolstered"
                | "bInterruptCurrentLine"
                | "bCanBeInterrupted"
                | "bCanQue"
                | "bIgnoreForwardInAir"
                | "bIsHonking"
                | "bIsJumping"
                | "bIsSprinting"
                | "bIsWaterJump"
                | "bIsWaterSprintBoost"
                | "bIsWaterSprintBoostPending"
                | "bIsTargeting"
                | "bPlayBit"
                | "bIsPlayingEmote"
                | "bStartedInteractSearch"
                | "bIsWaitingForEmoteInteraction"
                | "bIsSkydiving"
                | "bIsParachuteOpen"
                | "bIsParachuteForcedOpen"
                | "bIsSkydivingFromBus"
                | "bReplicatedIsInSlipperyMovement"
                | "bIsInAnyStorm"
                | "bIsSlopeSliding"
                | "bIsProxySimulationTimedOut"
                | "bIsInsideSafeZone"
                | "bIsOutsideSafeZone"
                | "bIsZiplining"
                | "bJumped"
                | "bNetMovementPrioritized"
                | "ForcePlayBit"
                | "IsStopped"
                | "SkipPositionCorrection"
                | "WeaponActivated"
                | "bIsInWaterVolume"
                | "bIsDefaultCharacter"
                | "bIsSkydivingFromLaunchPad"
                | "bInGliderRedeploy"
                | "bReplicatedIsInVortex"
                | "bIsTacticalSprinting" => {
                    let _ = r.read_bit();
                    true
                }
                "LocationOffset" | "RelativeScale3D" | "Location" => {
                    let _ = r.read_packed_vector(100, 30);
                    true
                }
                "RotationOffset" | "Rotation" => {
                    let _ = r.read_rotation_short();
                    true
                }
                "AttachSocket" | "BoneName" => {
                    let _ = r.read_fname();
                    true
                }
                "ExitSocketIndex"
                | "RemoteViewPitch"
                | "ReplicatedMovementMode"
                | "TeleportCounter"
                | "JumpFlashCount"
                | "SeatIndex"
                | "AccelerationZPack"
                | "DBNORevivalStacking" => {
                    let _ = r.read_byte();
                    true
                }
                "ReplayLastTransformUpdateTimeStamp"
                | "Position"
                | "SpawnImmunityTime"
                | "ForwardAlpha"
                | "RightAlpha"
                | "TurnDelta"
                | "SteerAlpha"
                | "GravityScale"
                | "VehicleApexZ"
                | "CapsuleRadiusAthena"
                | "CapsuleHalfHeightAthena"
                | "WalkSpeed"
                | "RunSpeed"
                | "SprintSpeed"
                | "CrouchedRunSpeed"
                | "CrouchedSprintSpeed"
                | "PlayRate"
                | "BlendTime"
                | "FlySpeed" => {
                    let _ = r.read_f32();
                    true
                }
                "LinearVelocity" => {
                    let _ = r.read_packed_vector(10, 24);
                    true
                }
                "CurrentMovementStyle" | "BuildingState" | "SimulatedProxyGameplayCues" => {
                    let _ = r.read_bits_to_u32(r.get_bits_left());
                    true
                }
                "PawnUniqueID" | "RepAnimMontageStartSection" => {
                    let _ = r.read_i32();
                    true
                }
                "WorldLookDir" => {
                    let _ = r.serialize_quantized_vector(VectorQuantization::RoundWholeNumber);
                    true
                }
                "PackedReplicatedSlopeAngles" | "AccelerationPack" => {
                    let _ = r.read_u16();
                    true
                }
                "RemoteViewData32"
                | "EntryTime"
                | "GravityFloorAltitude"
                | "GravityFloorWidth"
                | "GravityFloorGravityScalar"
                | "ServerWorldTimeRevivalTime" => {
                    let _ = r.read_u32();
                    true
                }
                "BannerIconId" | "BannerColorId" => {
                    let _ = r.read_fstring();
                    true
                }
                "ItemWraps" | "Dances" => self.skip_packed_int_array(r),
                "EncryptedPawnReplayData" => {
                    r.seek_end();
                    true
                }
                _ => false,
            },

            KnownGroup::GameState => match field_name {
                "RemoteRole"
                | "Role"
                | "GameModeClass"
                | "SpectatorClass"
                | "FortTimeOfDayManager"
                | "PoiManager"
                | "FeedbackManager"
                | "MissionManager"
                | "AnnouncementManager"
                | "WorldManager"
                | "MusicManagerSubclass"
                | "MusicManagerBank"
                | "PawnForReplayRelevancy"
                | "GlobalEnvironmentAbilityActor"
                | "UIMapManager"
                | "CreativePlotManager"
                | "PlayspaceManager"
                | "ItemCollector"
                | "SpecialActorData"
                | "SupplyDropWaveStartedSoundCue"
                | "TeamXPlayersLeft"
                | "SafeZoneIndicator"
                | "MapInfo"
                | "CreativeRealEstatePlotManager"
                | "ReplOverrideData"
                | "ServerToClientPreloadList"
                | "ClientVehicleClassesToLoad"
                | "VolumeManager" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "RecorderPlayerState" => {
                    game_state.recorder_player_state = Some(r.read_int_packed());
                    true
                }
                "DefaultBattleBus" | "WinningPlayerState" => {
                    let _ = r.read_int_packed();
                    true
                }
                "GoldenPoiLocationTags" => {
                    self.skip_gameplay_tag_container(r);
                    true
                }
                "bReplicatedHasBegunPlay"
                | "bAllowLayoutRequirementsFeature"
                | "bSafeZonePaused"
                | "bSkyTubesShuttingDown"
                | "bSkyTubesDisabled"
                | "bAllowUserPickedCosmeticBattleBus"
                | "bAircraftIsLocked"
                | "bStormReachedFinalPosition"
                | "SpectateAPartyMemberAvailable"
                | "bIsLargeTeamGame"
                | "bEnabled"
                | "bConnectedToRoot"
                | "bIsCustomMatch"
                | "bCraftingEnabled" => {
                    let _ = r.read_bit();
                    true
                }
                "ReplicatedWorldTimeSeconds"
                | "GameDifficulty"
                | "WarmupCountdownStartTime"
                | "WarmupCountdownEndTime"
                | "AircraftStartTime"
                | "SafeZonesStartTime"
                | "EndGameStartTime"
                | "EndGameKickPlayerTime"
                | "FlightSpeed"
                | "TimeTillFlightEnd"
                | "TimeTillDropStart"
                | "TimeTillDropEnd"
                | "HopRockDuration"
                | "DefaultGliderRedeployCanRedeploy"
                | "DefaultRedeployGliderLateralVelocityMult"
                | "DefaultRedeployGliderHeightLimit"
                | "DefaultParachuteDeployTraceForGroundDistance"
                | "DefaultRebootMachineHotfix"
                | "SignalInStormRegenSpeed"
                | "SignalInStormLostSpeed"
                | "StormCNDamageVulnerabilityLevel0"
                | "StormCNDamageVulnerabilityLevel1"
                | "StormCNDamageVulnerabilityLevel2"
                | "StormCNDamageVulnerabilityLevel3"
                | "PlayersLoaded"
                | "MatchStartTime" => {
                    let _ = r.read_f32();
                    true
                }
                "ReplicatedWorldTimeSecondsDouble"
                | "ReplicatedWorldRealTimeSecondsDouble"
                | "RealMatchStartTime" => {
                    let _ = r.read_f64();
                    true
                }
                "MatchState" => {
                    let _ = r.read_fname();
                    true
                }
                "ElapsedTime"
                | "WorldLevel"
                | "CraftingBonus"
                | "TeamCount"
                | "TeamSize"
                | "GameFlagData"
                | "WorldDaysElapsed"
                | "SpawnPointsCap"
                | "SpawnPointsAllocated"
                | "TotalPlayerStructures"
                | "RoundTimeAccumulated"
                | "RoundTimeCriticalThreshold"
                | "ServerChangelistNumber"
                | "PlayersLeft"
                | "TeamsLeft"
                | "LobbyAction"
                | "EventId"
                | "PlayerBotsLeft" => {
                    let _ = r.read_i32();
                    true
                }
                "GameSessionId" => {
                    game_state.game_session_id = Some(r.read_fstring());
                    true
                }
                "UtcTimeStartedMatch" => {
                    game_state.utc_time_started_match = Some(r.read_u64());
                    true
                }
                "AdditionalPlaylistLevelsStreamed" => self.skip_fname_array(r),
                "PlayerSharedMaxTrapAttributes" => self.skip_primitive_array(r, 32),
                "WinningPlayerList" => self.skip_primitive_array(r, 32),
                "Aircrafts"
                | "ActiveTeamNums"
                | "TrackedCosmetics"
                | "VariantUsageByCosmetic"
                | "PrioritizedCosmeticIndices"
                | "Mappings" => self.skip_packed_int_array(r),
                "TeamFlightPaths" => {
                    // Aircraft has ParseMode.Ignore -> ReadArrayField returns null -> incompatible
                    false
                }
                "GameplayState"
                | "ServerStability"
                | "StormCapState"
                | "GamePhase"
                | "AirCraftBehavior"
                | "EventTournamentRound"
                | "GameServerNodeType" => {
                    let _ = r.read_bits_to_u32(r.get_bits_left());
                    true
                }
                "ServerGameplayTagIndexHash"
                | "WinningTeam"
                | "WinningScore"
                | "CurrentHighScore"
                | "CurrentHighScoreTeam"
                | "MutatorGenericInt" => {
                    let _ = r.read_u32();
                    true
                }
                "FlightStartLocation" => {
                    let _ = r.read_packed_vector(100, 30);
                    true
                }
                "FlightStartRotation" => {
                    let _ = r.read_rotation_short();
                    true
                }
                "SafeZonePhase" => {
                    let _ = r.read_byte();
                    true
                }
                _ => false,
            },

            KnownGroup::ActiveGameplayModifier => match field_name {
                "ModifierDef" => {
                    let _ = r.read_int_packed();
                    true
                }
                _ => false,
            },

            KnownGroup::ReplayPC => match field_name {
                "Role" | "RemoteRole" => {
                    r.skip_bits(r.get_bits_left());
                    true
                }
                "PlayerState" => {
                    let _ = r.read_int_packed();
                    true
                }
                "SpawnLocation" => {
                    let _ = r.read_fvector();
                    true
                }
                _ => false,
            },

            KnownGroup::PlaylistInfo
            | KnownGroup::SupplyDropLlama
            | KnownGroup::SupplyDrop
            | KnownGroup::FortPoiManager
            | KnownGroup::SafeZoneIndicator
            | KnownGroup::BaseWeapon => false,
        }
    }

    fn read_seat_slots_array(
        &self,
        group_name: &str,
        r: &mut BitReader,
    ) -> Option<Vec<Option<u32>>> {
        let group = self.guid_cache.export_group_map.get(group_name)?;
        let array_indexes = r.read_int_packed() as usize;
        if array_indexes > 64 {
            return None;
        }
        let mut arr: Vec<Option<u32>> = vec![None; array_indexes];

        loop {
            let mut index = r.read_int_packed();
            if index == 0 {
                if r.get_bits_left() == 8 {
                    let terminator = r.read_int_packed();
                    if terminator != 0 {
                        return Some(arr);
                    }
                }
                return Some(arr);
            }
            index -= 1;
            if (index as usize) >= array_indexes {
                return Some(arr);
            }

            let mut player_val: Option<u32> = None;
            loop {
                let mut handle = r.read_int_packed();
                if handle == 0 {
                    break;
                }
                handle -= 1;
                if (handle as usize) >= group.exports.len() {
                    return Some(arr);
                }
                let num_bits = r.read_int_packed() as usize;
                if num_bits == 0 {
                    continue;
                }
                let Some(ref exp) = group.exports[handle as usize] else {
                    r.skip_bits(num_bits);
                    continue;
                };
                r.set_temp_end(num_bits, EndIndex::ReadArrayField);
                if exp.name == "player" {
                    player_val = Some(r.read_int_packed());
                }
                r.restore_temp_end(EndIndex::ReadArrayField);
            }
            arr[index as usize] = player_val;
        }
    }

    fn skip_packed_int_array(&self, r: &mut BitReader) -> bool {
        let array_indexes = r.read_int_packed();
        loop {
            let mut index = r.read_int_packed();
            if index == 0 {
                if r.get_bits_left() == 8 {
                    let _ = r.read_int_packed();
                }
                return true;
            }
            index -= 1;
            if index >= array_indexes {
                return true;
            }
            loop {
                let handle = r.read_int_packed();
                if handle == 0 {
                    break;
                }
                let num_bits = r.read_int_packed() as usize;
                if num_bits == 0 {
                    continue;
                }
                r.set_temp_end(num_bits, EndIndex::ReadArrayField);
                let _ = r.read_int_packed();
                r.restore_temp_end(EndIndex::ReadArrayField);
            }
        }
    }

    fn skip_fname_array(&self, r: &mut BitReader) -> bool {
        let array_indexes = r.read_int_packed();
        loop {
            let mut index = r.read_int_packed();
            if index == 0 {
                if r.get_bits_left() == 8 {
                    let _ = r.read_int_packed();
                }
                return true;
            }
            index -= 1;
            if index >= array_indexes {
                return true;
            }
            loop {
                let handle = r.read_int_packed();
                if handle == 0 {
                    break;
                }
                let num_bits = r.read_int_packed() as usize;
                if num_bits == 0 {
                    continue;
                }
                r.set_temp_end(num_bits, EndIndex::ReadArrayField);
                let _ = r.read_fname();
                r.restore_temp_end(EndIndex::ReadArrayField);
            }
        }
    }

    fn skip_primitive_array(&self, r: &mut BitReader, elem_bits: usize) -> bool {
        let array_indexes = r.read_int_packed();
        loop {
            let mut index = r.read_int_packed();
            if index == 0 {
                if r.get_bits_left() == 8 {
                    let _ = r.read_int_packed();
                }
                return true;
            }
            index -= 1;
            if index >= array_indexes {
                return true;
            }
            loop {
                let handle = r.read_int_packed();
                if handle == 0 {
                    break;
                }
                let num_bits = r.read_int_packed() as usize;
                if num_bits == 0 {
                    continue;
                }
                r.set_temp_end(num_bits, EndIndex::ReadArrayField);
                r.skip_bits(elem_bits);
                r.restore_temp_end(EndIndex::ReadArrayField);
            }
        }
    }

    fn skip_gameplay_tag_container(&self, r: &mut BitReader) {
        if r.read_bit() {
            return;
        }
        let num_tags = r.read_bits_to_u32(7);
        for _ in 0..num_tags {
            let b_serialize_rep_method = r.engine_network_version >= 36;
            let mut b_use_fast = true;
            let mut b_use_dynamic = false;
            if b_serialize_rep_method {
                b_use_fast = r.read_bit();
                if !b_use_fast {
                    b_use_dynamic = r.read_bit();
                }
            }
            if b_use_fast {
                let _ = r.read_int_packed();
            } else if b_use_dynamic {
                let tag_index = r.read_int_packed();
                if tag_index > 0 {
                    let _ = r.read_bit();
                }
            }
        }
    }

    // ---- ChestScanReader callbacks ----

    fn path_of(&self, guid: Option<u32>) -> &str {
        match guid {
            None | Some(0) => "",
            Some(g) => self
                .guid_cache
                .guid_to_path_name
                .get(&g)
                .map(|s| s.as_str())
                .unwrap_or(""),
        }
    }

    fn on_channel_opened(&mut self, channel_index: u32, _actor_guid: u32) {
        let (archetype_id, actor_id, loc_opt) = {
            let ch = self.channels[channel_index as usize].as_ref();
            (
                ch.and_then(|c| c.archetype_id()),
                ch.and_then(|c| c.actor_id()),
                ch.and_then(|c| c.actor.as_ref().and_then(|a| a.location)),
            )
        };

        let archetype = self.path_of(archetype_id).to_string();
        let actor_path = self.path_of(actor_id).to_string();
        let label = clean_name(if !archetype.is_empty() {
            &archetype
        } else {
            &actor_path
        });

        let key = if !actor_path.is_empty() {
            actor_path
        } else if let Some(loc) = loc_opt
            && is_chest(&label)
            && !label.to_ascii_lowercase().starts_with("prop_")
        {
            format!("{}@{:.0},{:.0}", label, loc.x, loc.y)
        } else {
            match actor_id {
                Some(id) => format!("guid:{}", id),
                None => "guid:".to_string(),
            }
        };

        self.channel_key.insert(channel_index, key);
        self.channel_label.insert(channel_index, label);
        self.channel_open_at.insert(channel_index, self.current_time);
        self.channel_searched.remove(&channel_index);
        self.last_searcher.remove(&channel_index);
        self.pending_opener.remove(&channel_index);
        self.prize_channels.remove(&channel_index);
        self.orphan_channels.remove(&channel_index);
    }

    fn on_net_guid_exported(&mut self, net_guid: u32, path_name: &str) {
        if path_name.contains("TreasureHandler") {
            self.handler_guids.insert(net_guid, clean_name(path_name));
        } else if path_name.contains("WID_") && is_gizmo_item(path_name) {
            self.gizmo_items
                .entry(clean_name(path_name))
                .or_insert(self.current_time);
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn on_export_read(
        &mut self,
        channel_index: u32,
        known: KnownGroup,
        container: BaseContainerData,
        prize_chest: TreasureHuntChestData,
        pawn: PlayerPawnData,
        vehicle: GenericVehicleData,
        seats: SeatComponentData,
        pickup: FortPickupData,
        player_state: FortPlayerStateData,
        game_state: GameStateData,
    ) {
        match known {
            KnownGroup::GenericContainer => {
                self.read_container(channel_index, container);
            }
            KnownGroup::TreasureHuntChest => {
                if prize_chest.b_success != Some(false) {
                    if let Some(ref raw) = prize_chest.prize_template_bytes {
                        if let Some(id) = Self::read_prize_id(raw) {
                            if self.prize_channels.insert(channel_index) {
                                self.prizes.push(Prize {
                                    time: self.current_time,
                                    id,
                                    pawn: prize_chest.opening_pawn.or(prize_chest.instigator),
                                });
                            }
                        }
                    }
                }
            }
            KnownGroup::PlayerPawn => {
                let Some(pawn_id) = self.channels[channel_index as usize]
                    .as_ref()
                    .and_then(|c| c.actor_id())
                else {
                    return;
                };
                if let Some(ps) = pawn.player_state
                    && ps != 0
                {
                    self.pawn_to_state.insert(pawn_id, ps);
                }
                if let Some(mv) = pawn.replicated_movement {
                    Self::track(
                        &mut self.pawn_track,
                        pawn_id,
                        mv.location,
                        self.current_time,
                        0.5,
                    );
                }
            }
            KnownGroup::GenericVehicle => {
                if let Some(drive) = vehicle.replicated_movement {
                    if let Some(vehicle_id) = self.channels[channel_index as usize]
                        .as_ref()
                        .and_then(|c| c.actor_id())
                    {
                        Self::track(
                            &mut self.vehicle_track,
                            vehicle_id,
                            drive.location,
                            self.current_time,
                            0.25,
                        );
                    }
                }
            }
            KnownGroup::SeatComponent => {
                if let Some(slots) = seats.player_slots {
                    if let Some(seat_vehicle) = self.channels[channel_index as usize]
                        .as_ref()
                        .and_then(|c| c.actor_id())
                    {
                        for (seat, slot_opt) in slots.into_iter().enumerate() {
                            let Some(occupant) = slot_opt else {
                                continue;
                            };
                            if let Some(leaving_idx) = self.seated.remove(&(seat_vehicle, seat)) {
                                self.seat_spans[leaving_idx].to = self.current_time;
                            }
                            if occupant == 0 {
                                continue;
                            }
                            let idx = self.seat_spans.len();
                            self.seat_spans.push(SeatSpan {
                                pawn: occupant,
                                vehicle: seat_vehicle,
                                from: self.current_time,
                                to: f32::MAX,
                            });
                            self.seated.insert((seat_vehicle, seat), idx);
                        }
                    }
                }
            }
            KnownGroup::FortPickup => {
                if pickup.b_tossed_from_container == Some(true) {
                    if let Some(container_owner) = pickup.owner
                        && container_owner != 0
                    {
                        if let Some(spawn) = self.channels[channel_index as usize]
                            .as_ref()
                            .and_then(|c| c.actor.as_ref().and_then(|a| a.location))
                        {
                            self.tosses.push((
                                self.current_time,
                                Pt {
                                    t: self.current_time,
                                    x: spawn.x as f32,
                                    y: spawn.y as f32,
                                    z: spawn.z as f32,
                                },
                                container_owner,
                            ));
                        }
                    }
                }
            }
            KnownGroup::FortPlayerState => {
                // Update BuilderPlayerData
                if player_state.b_only_spectator == Some(true) {
                    self.only_spectating_channels.insert(channel_index);
                } else if !self.only_spectating_channels.contains(&channel_index) {
                    let pid_opt = match player_state.player_id_u32 {
                        Some(u) => Some(u as i32),
                        None => player_state.player_id_i32,
                    };
                    self.builder_players
                        .entry(channel_index)
                        .or_insert_with(|| BuilderPlayerData {
                            id: pid_opt,
                            player_name: None,
                        });
                }

                let Some(state_id) = self.channels[channel_index as usize]
                    .as_ref()
                    .and_then(|c| c.actor_id())
                else {
                    return;
                };
                if !self.players.contains_key(&state_id) {
                    self.player_order.push(state_id);
                }
                let player = self.players.entry(state_id).or_default();
                let pid_opt = match player_state.player_id_u32 {
                    Some(u) => Some(u as i32),
                    None => player_state.player_id_i32,
                };
                if let Some(pid) = pid_opt {
                    player.player_id = Some(pid as i64);
                }
                if let Some(team) = player_state.team_index {
                    player.team = Some(team);
                }
            }
            KnownGroup::GameState => {
                if self.game_session_id.is_none() {
                    if let Some(gsid) = game_state.game_session_id {
                        if !gsid.is_empty() {
                            self.game_session_id = Some(gsid);
                        }
                    }
                }
                if self.utc_time_started_match.is_none() {
                    if let Some(ticks) = game_state.utc_time_started_match {
                        // FDateTime ticks (100ns since 0001-01-01)
                        let unix_ticks = (ticks as i64) - 621_355_968_000_000_000;
                        let secs = unix_ticks.div_euclid(10_000_000);
                        let nsecs = (unix_ticks.rem_euclid(10_000_000) * 100) as u32;
                        self.utc_time_started_match = Utc.timestamp_opt(secs, nsecs).single();
                    }
                }
                if self.recorder_id.is_none() {
                    if let Some(rec) = game_state.recorder_player_state
                        && rec != 0
                    {
                        self.recorder_id = Some(rec);
                    }
                }
            }
            _ => {}
        }
    }

    fn stable_key(key: &str) -> bool {
        !key.starts_with("guid:") && !key.starts_with("ch:")
    }

    fn read_container(&mut self, channel_index: u32, container: BaseContainerData) {
        if let Some(searcher) = container.searching_pawn
            && searcher != 0
        {
            self.last_searcher.insert(channel_index, searcher);
            if let Some(idx) = self.pending_opener.remove(&channel_index) {
                if self.searches[idx].pawn.is_none() {
                    self.searches[idx].pawn = Some(searcher);
                    let k = self.searches[idx].key.clone();
                    self.last_opener.insert(k, Some(searcher));
                }
            }
        }

        let key = self
            .channel_key
            .get(&channel_index)
            .cloned()
            .unwrap_or_else(|| format!("ch:{}", channel_index));
        let label = self
            .channel_label
            .get(&channel_index)
            .cloned()
            .unwrap_or_else(|| "?".to_string());
        let open_at = self
            .channel_open_at
            .get(&channel_index)
            .copied()
            .unwrap_or(f32::MIN);
        let stale = self.current_time - open_at < 0.5;

        if container.b_already_searched == Some(false) {
            self.channel_searched.insert(channel_index, false);
        } else if container.b_already_searched == Some(true)
            && !self
                .channel_searched
                .get(&channel_index)
                .copied()
                .unwrap_or(false)
        {
            self.channel_searched.insert(channel_index, true);
            let opened_by = self.last_searcher.get(&channel_index).copied();
            let seen_again = stale
                && if Self::stable_key(&key) {
                    self.last_opener
                        .get(&key)
                        .is_some_and(|&prev| prev == opened_by)
                } else {
                    self.searched.contains(&key)
                };

            if !seen_again {
                self.searched.insert(key.clone());
                self.last_opener.insert(key.clone(), opened_by);
                if !stale || Self::stable_key(&key) {
                    self.searches.push(Search {
                        time: self.current_time,
                        key,
                        label,
                        pawn: opened_by,
                    });
                    if opened_by.is_none() {
                        self.pending_opener
                            .insert(channel_index, self.searches.len() - 1);
                    }
                }
            }
        } else if container.b_destroyed
            && let Some(destroyer) = container.searching_pawn
            && destroyer != 0
            && (self.searched.insert(key.clone())
                || self
                    .channel_searched
                    .get(&channel_index)
                    .is_some_and(|&was| !was))
        {
            self.channel_searched.insert(channel_index, true);
            self.last_opener.insert(key.clone(), Some(destroyer));
            if !stale || Self::stable_key(&key) {
                self.searches.push(Search {
                    time: self.current_time,
                    key,
                    label,
                    pawn: Some(destroyer),
                });
            }
        } else if container.b_destroyed
            && container.searching_pawn.unwrap_or(0) == 0
            && !stale
            && Self::stable_key(&key)
            && (!self.searched.contains(&key)
                || self
                    .channel_searched
                    .get(&channel_index)
                    .is_some_and(|&open| !open))
            && let Some(orphan_actor) = self.channels[channel_index as usize]
                .as_ref()
                .and_then(|c| c.actor_id())
            && self.orphan_channels.insert(channel_index)
        {
            self.orphans.push(Orphan {
                time: self.current_time,
                key,
                label,
                actor: orphan_actor,
            });
        }
    }

    fn track(
        tracks: &mut HashMap<u32, Vec<Pt>>,
        id: u32,
        loc: FVector,
        now: f32,
        min_interval: f32,
    ) {
        let track = tracks.entry(id).or_default();
        if track.is_empty() || now - track.last().unwrap().t >= min_interval {
            track.push(Pt {
                t: now,
                x: loc.x as f32,
                y: loc.y as f32,
                z: loc.z as f32,
            });
        }
    }

    fn at_track(track: Option<&Vec<Pt>>, t: f32, max_gap: f32) -> Option<Pt> {
        let track = track?;
        if track.is_empty() {
            return None;
        }
        let mut lo = 0usize;
        let mut hi = track.len() - 1;
        while lo < hi {
            let mid = (lo + hi) / 2;
            if track[mid].t < t {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }
        let after = track[lo];
        if lo == 0 || after.t <= t {
            return if (after.t - t).abs() <= max_gap {
                Some(after)
            } else {
                None
            };
        }
        let before = track[lo - 1];
        if after.t - before.t <= max_gap {
            let f = (t - before.t) / (after.t - before.t);
            return Some(Pt {
                t,
                x: before.x + (after.x - before.x) * f,
                y: before.y + (after.y - before.y) * f,
                z: before.z + (after.z - before.z) * f,
            });
        }
        let nearest = if t - before.t <= after.t - t {
            before
        } else {
            after
        };
        if (nearest.t - t).abs() <= max_gap {
            Some(nearest)
        } else {
            None
        }
    }

    fn position_of(&self, pawn: u32, t: f32) -> Option<Pt> {
        for span in &self.seat_spans {
            if span.pawn == pawn && t >= span.from - 0.5 && t <= span.to + 0.5 {
                if let Some(in_veh) =
                    Self::at_track(self.vehicle_track.get(&span.vehicle), t, 3.0)
                {
                    return Some(in_veh);
                }
            }
        }
        Self::at_track(self.pawn_track.get(&pawn), t, 3.0)
    }

    fn distance(a: Pt, b: Pt) -> f32 {
        ((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z))
            .sqrt()
    }

    fn resolve_orphans(&mut self) {
        let orphans = std::mem::take(&mut self.orphans);
        for orphan in orphans {
            let loot: Vec<Pt> = self
                .tosses
                .iter()
                .filter(|&&(t, _, owner)| owner == orphan.actor && (t - orphan.time).abs() <= 2.0)
                .map(|&(_, loc, _)| loc)
                .collect();
            if loot.is_empty() {
                continue;
            }

            let mut nearest = 0u32;
            let mut nearest_dist = 450.0f32;
            for &pawn in self.pawn_to_state.keys() {
                let Some(at) = self.position_of(pawn, orphan.time) else {
                    continue;
                };
                let d = loot
                    .iter()
                    .map(|&l| Self::distance(at, l))
                    .fold(f32::MAX, f32::min);
                if d < nearest_dist {
                    nearest_dist = d;
                    nearest = pawn;
                }
            }
            if nearest == 0 {
                continue;
            }

            let orphan_tag = format!("{}#orphan{:.0}", orphan.key, orphan.time);
            if self.searched.insert(orphan_tag) {
                self.searches.push(Search {
                    time: orphan.time,
                    key: orphan.key,
                    label: orphan.label,
                    pawn: Some(nearest),
                });
            }
        }
    }

    fn read_ability_grants(&mut self, channel: u32, data: &[u8], bits: usize) {
        let mut pos = 1usize; // bSupportsFastArrayDeltaStructSerialization

        let read_bits = |pos: &mut usize, n: usize| -> Option<u32> {
            if *pos + n > bits {
                return None;
            }
            let mut v = 0u32;
            for k in 0..n {
                let bit = ((data[*pos >> 3] >> (*pos & 7)) & 1) as u32;
                v |= bit << k;
                *pos += 1;
            }
            Some(v)
        };

        let read_packed = |pos: &mut usize| -> Option<u32> {
            let mut v = 0u32;
            let mut shift = 0u32;
            while shift < 35 {
                let b = read_bits(pos, 8)?;
                v |= (b >> 1) << shift;
                if (b & 1) == 0 {
                    return Some(v);
                }
                shift += 7;
            }
            None
        };

        let Some(_) = read_bits(&mut pos, 32) else {
            return;
        };
        let Some(_) = read_bits(&mut pos, 32) else {
            return;
        };
        let Some(deletes) = read_bits(&mut pos, 32) else {
            return;
        };
        let Some(changed) = read_bits(&mut pos, 32) else {
            return;
        };
        if deletes > 512 || changed > 512 || bits < 129 {
            return;
        }
        pos += (deletes as usize) * 32;

        for _ in 0..changed {
            let Some(element_id) = read_bits(&mut pos, 32) else {
                return;
            };
            let element_id = element_id as i32;
            loop {
                let Some(handle) = read_packed(&mut pos) else {
                    return;
                };
                if handle == 0 {
                    break;
                }
                let Some(num_bits) = read_packed(&mut pos) else {
                    return;
                };
                let num_bits = num_bits as usize;
                if pos + num_bits > bits {
                    return;
                }
                if !matches!(num_bits, 8 | 16 | 24 | 32 | 40) {
                    pos += num_bits;
                    continue;
                }

                let start = pos;
                let mut guid = 0u32;
                let mut shift = 0u32;
                while pos < start + num_bits {
                    let Some(b) = read_bits(&mut pos, 8) else {
                        return;
                    };
                    guid |= (b >> 1) << shift;
                    if (b & 1) == 0 {
                        break;
                    }
                    shift += 7;
                }
                pos = start + num_bits;

                if !self.handler_guids.contains_key(&guid) {
                    continue;
                }
                let owner = self.channels[channel as usize]
                    .as_ref()
                    .and_then(|c| c.actor_id())
                    .unwrap_or(0);
                if !self
                    .grants
                    .iter()
                    .any(|g| g.player_state == owner && g.element_id == element_id)
                {
                    self.grants.push(Grant {
                        time: self.current_time,
                        player_state: owner,
                        element_id,
                    });
                }
            }
        }
    }

    fn read_prize_id(raw: &[u8]) -> Option<String> {
        if raw.is_empty() {
            return None;
        }
        let ascii: String = raw
            .iter()
            .map(|&b| {
                if (32..127).contains(&b) {
                    b as char
                } else {
                    ' '
                }
            })
            .collect();
        PRIZE_ID_RE.find(&ascii).map(|m| m.as_str().to_string())
    }

    fn match_id(&self) -> String {
        if let Some(ref gsid) = self.game_session_id {
            if !gsid.is_empty() {
                return gsid.clone();
            }
        }
        let time_str = self
            .utc_time_started_match
            .map(|t| t.to_rfc3339())
            .unwrap_or_default();
        let rec_str = self.recorder_id.map(|r| r.to_string()).unwrap_or_default();
        format!("{}|{}", time_str, rec_str)
    }

    pub fn classify(&mut self) -> MatchChestCounts {
        let mut result = MatchChestCounts {
            match_id: self.match_id(),
            time: self.utc_time_started_match,
            you: 0,
            mates: Vec::new(),
            prizes: Vec::new(),
        };

        let Some(me) = self.recorder_id else {
            return result;
        };
        self.resolve_orphans();

        let mut names: HashMap<i64, String> = HashMap::new();
        for bp in self.builder_players.values() {
            if let (Some(id), Some(pname)) = (bp.id, bp.player_name.as_ref()) {
                let trimmed = pname.trim();
                if !trimmed.is_empty() {
                    names.insert(id as i64, trimmed.to_string());
                }
            }
        }
        for p in self.players.values_mut() {
            if let Some(pid) = p.player_id {
                if let Some(n) = names.get(&pid) {
                    p.name = Some(n.clone());
                }
            }
        }

        let self_info = self.players.get(&me).cloned();
        let my_team = self_info.as_ref().and_then(|s| s.team);
        let self_name = self_info.as_ref().and_then(|s| s.name.clone());

        let state_of = |pawn: Option<u32>| -> Option<u32> {
            pawn.and_then(|p| self.pawn_to_state.get(&p).copied())
        };
        let is_mate = |state: u32| -> bool {
            state != me
                && my_team.is_some()
                && self.players.get(&state).is_some_and(|w| w.team == my_team)
        };

        let mut per_player: HashMap<u32, i32> = HashMap::new();
        for s in &self.searches {
            if is_chest(&s.label) {
                if let Some(state) = state_of(s.pawn) {
                    *per_player.entry(state).or_insert(0) += 1;
                }
            }
        }
        result.you = per_player.get(&me).copied().unwrap_or(0);

        if my_team.is_some() {
            let mut by_name: HashMap<String, i32> = HashMap::new();
            let mut name_order: Vec<String> = Vec::new();
            for &state_key in &self.player_order {
                if let Some(p_val) = self.players.get(&state_key) {
                    if is_mate(state_key) {
                        if let Some(ref n) = p_val.name {
                            if self_name.as_deref() != Some(n.as_str()) {
                                let c = per_player.get(&state_key).copied().unwrap_or(0);
                                if !by_name.contains_key(n) {
                                    name_order.push(n.clone());
                                }
                                *by_name.entry(n.clone()).or_insert(0) += c;
                            }
                        }
                    }
                }
            }
            let mut mates: Vec<MateCount> = name_order
                .into_iter()
                .map(|name| {
                    let chests = by_name[&name];
                    MateCount { name, chests }
                })
                .collect();
            mates.sort_by(|a, b| b.chests.cmp(&a.chests));
            result.mates = mates;
        }

        for prize in &self.prizes {
            let finder = state_of(prize.pawn);
            let mine = finder == Some(me);
            let by = finder
                .and_then(|f| self.players.get(&f))
                .and_then(|w| w.name.clone())
                .unwrap_or_default();
            let team = !mine && finder.is_some_and(&is_mate);
            result.prizes.push(PrizeFind {
                prize_id: prize.id.clone(),
                by,
                mine,
                team,
            });
        }

        let mut my_grants: Vec<&Grant> = self
            .grants
            .iter()
            .filter(|g| g.player_state == me)
            .collect();
        my_grants.sort_by(|a, b| a.time.partial_cmp(&b.time).unwrap_or(std::cmp::Ordering::Equal));

        for grant in my_grants {
            if self
                .prizes
                .iter()
                .any(|p| (p.time - grant.time).abs() <= 5.0)
            {
                continue;
            }

            let chest = self
                .searches
                .iter()
                .filter(|s| {
                    s.time >= grant.time - 3.0
                        && (s.time <= grant.time + 2.0
                            || (s.time <= grant.time + 4.0
                                && s.label.to_ascii_lowercase().contains("buried")))
                        && state_of(s.pawn).is_some_and(|st| st == me || is_mate(st))
                })
                .min_by(|a, b| {
                    (a.time - grant.time)
                        .abs()
                        .partial_cmp(&(b.time - grant.time).abs())
                        .unwrap_or(std::cmp::Ordering::Equal)
                });

            let opener = chest.and_then(|c| state_of(c.pawn));
            let item = self
                .gizmo_items
                .iter()
                .find(|&(_, &t)| (t - grant.time).abs() <= 1.0)
                .map(|(k, _)| k.as_str())
                .unwrap_or("");

            let by = if opener == Some(me) {
                "you".to_string()
            } else if let Some(o) = opener
                && let Some(mate) = self.players.get(&o)
            {
                mate.name.clone().unwrap_or_default()
            } else {
                String::new()
            };

            result.prizes.push(PrizeFind {
                prize_id: format!("{}{}", GIZMO_PREFIX, item),
                by,
                mine: true,
                team: true,
            });
        }

        result
    }
}

// ---- ReplayScanner ----

#[derive(Clone, Debug, Default)]
pub struct RunReport {
    pub total: usize,
    pub parsed: usize,
    pub failed: usize,
    pub new_matches: usize,
    pub already_counted: usize,
    pub skipped_mode: usize,
}

pub fn is_counted_playlist(playlist: Option<&str>) -> bool {
    let Some(p) = playlist else {
        return false;
    };
    matches!(
        p.to_ascii_lowercase().as_str(),
        "playlist_defaultsolo"
            | "playlist_defaultduo"
            | "playlist_defaulttrio"
            | "playlist_defaultsquad"
            | "playlist_nobuildbr_solo"
            | "playlist_nobuildbr_duo"
            | "playlist_nobuildbr_trio"
            | "playlist_nobuildbr_squad"
    )
}

pub fn find_replays(demos_dir: Option<&Path>) -> Vec<PathBuf> {
    let default_dir = crate::stats::default_demos_path();
    let dir = demos_dir.unwrap_or(&default_dir);
    let Ok(entries) = fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut files: Vec<(PathBuf, std::time::SystemTime)> = entries
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| {
            p.extension()
                .and_then(|s| s.to_str())
                .is_some_and(|ext| ext.eq_ignore_ascii_case("replay"))
        })
        .map(|p| {
            let mtime = fs::metadata(&p)
                .and_then(|m| m.modified())
                .unwrap_or(std::time::UNIX_EPOCH);
            (p, mtime)
        })
        .collect();

    files.sort_by(|a, b| b.1.cmp(&a.1));
    files.into_iter().map(|(p, _)| p).collect()
}

pub fn file_scan_key(path: &Path) -> String {
    let name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or_default();
    match fs::metadata(path) {
        Ok(m) => format!("{}|{}", name, m.len()),
        Err(_) => name.to_string(),
    }
}

/// Parallel multi-core replay scanner using Rayon!
/// Parses all unscanned replays in parallel across CPU cores, then folds results in deterministic order.
pub fn scan_replays<F>(
    files: &[PathBuf],
    stats: &mut LifetimeStats,
    mut on_progress: Option<F>,
) -> RunReport
where
    F: FnMut(usize, usize, &str, Option<&str>),
{
    let mut run = RunReport {
        total: files.len(),
        ..Default::default()
    };

    // Identify which files are already scanned vs need parsing
    let mut to_parse: Vec<(usize, PathBuf, String)> = Vec::new();
    for (i, file) in files.iter().enumerate() {
        let key = file_scan_key(file);
        if stats.scanned_files.contains(&key) {
            run.already_counted += 1;
            if let Some(ref mut cb) = on_progress {
                let fname = file.file_name().and_then(|n| n.to_str()).unwrap_or("");
                cb(i + 1, files.len(), fname, None);
            }
        } else {
            to_parse.push((i, file.clone(), key));
        }
    }

    if to_parse.is_empty() {
        return run;
    }

    // Parse all new replays in parallel across CPU cores
    enum ParseOutcome {
        Counted {
            counts: MatchChestCounts,
            fallback_time: DateTime<Utc>,
        },
        SkippedMode,
        Failed(String),
    }

    let outcomes: Vec<(usize, PathBuf, String, ParseOutcome)> = to_parse
        .into_par_iter()
        .map(|(idx, path, key)| {
            let mut reader = ChestScanReader::new();
            let res = reader.read_replay_file(&path);
            let outcome = match res {
                Ok(()) => {
                    if reader.is_partial_replay || reader.info.is_live {
                        ParseOutcome::Failed("This match is still being recorded.".to_string())
                    } else if !is_counted_playlist(reader.current_playlist.as_deref()) {
                        ParseOutcome::SkippedMode
                    } else {
                        let counts = reader.classify();
                        let fallback_time = fs::metadata(&path)
                            .and_then(|m| m.modified())
                            .ok()
                            .map(DateTime::<Utc>::from)
                            .unwrap_or_else(Utc::now);
                        ParseOutcome::Counted {
                            counts,
                            fallback_time,
                        }
                    }
                }
                Err(e) => ParseOutcome::Failed(e.to_string()),
            };
            (idx, path, key, outcome)
        })
        .collect();

    for (done_idx, (_, path, key, outcome)) in outcomes.into_iter().enumerate() {
        let fname = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
        let mut err_msg: Option<String> = None;

        match outcome {
            ParseOutcome::Counted {
                counts,
                fallback_time,
            } => {
                run.parsed += 1;
                stats.scanned_files.insert(key);
                if stats.add_match(counts, fallback_time) {
                    run.new_matches += 1;
                } else {
                    run.already_counted += 1;
                }
            }
            ParseOutcome::SkippedMode => {
                run.parsed += 1;
                stats.scanned_files.insert(key);
                run.skipped_mode += 1;
            }
            ParseOutcome::Failed(msg) => {
                run.failed += 1;
                err_msg = Some(msg);
            }
        }

        if let Some(ref mut cb) = on_progress {
            cb(
                run.already_counted + done_idx + 1,
                files.len(),
                fname,
                err_msg.as_deref(),
            );
        }
    }

    run
}
