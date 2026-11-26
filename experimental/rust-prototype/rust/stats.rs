//! Persistent storage for LifetimeStats, OverlaySettings, and AppSettings.
//! The Rust prototype uses its own directory because its history schema predates the desktop app.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::PathBuf;

pub const CURRENT_VERSION: i32 = 6;
pub const DOMAIN: &str = "master-yks";
pub const DEFAULT_ACCENT: &str = "#F3F1EC";

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct MateCount {
    #[serde(rename = "Name")]
    pub name: String,
    #[serde(rename = "Chests")]
    pub chests: i32,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct PrizeFind {
    #[serde(rename = "PrizeId")]
    pub prize_id: String,
    #[serde(rename = "By")]
    pub by: String,
    #[serde(rename = "Mine")]
    pub mine: bool,
    #[serde(rename = "Team")]
    pub team: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct MatchRecord {
    #[serde(rename = "MatchId")]
    pub match_id: String,
    #[serde(rename = "Time")]
    pub time: DateTime<Utc>,
    #[serde(rename = "You")]
    pub you: i32,
    #[serde(rename = "Mates", default)]
    pub mates: Vec<MateCount>,
    #[serde(rename = "Prizes", default)]
    pub prizes: Vec<PrizeFind>,
}

#[derive(Clone, Debug)]
pub struct MatchChestCounts {
    pub match_id: String,
    pub time: Option<DateTime<Utc>>,
    pub you: i32,
    pub mates: Vec<MateCount>,
    pub prizes: Vec<PrizeFind>,
}

#[derive(Clone, Debug)]
pub struct MateTotal {
    pub name: String,
    pub matches: usize,
    pub chests: i64,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Period {
    pub matches: usize,
    pub your_chests: i64,
    pub avg_you: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LifetimeStats {
    #[serde(rename = "Version", default)]
    pub version: i32,
    #[serde(rename = "Records", default)]
    pub records: Vec<MatchRecord>,
    #[serde(rename = "ScannedFiles", default)]
    pub scanned_files: HashSet<String>,
}

impl Default for LifetimeStats {
    fn default() -> Self {
        Self {
            version: CURRENT_VERSION,
            records: Vec::new(),
            scanned_files: HashSet::new(),
        }
    }
}

impl LifetimeStats {
    pub fn matches(&self) -> usize {
        self.records.len()
    }

    pub fn your_chests(&self) -> i64 {
        self.records.iter().map(|r| r.you as i64).sum()
    }

    pub fn avg_you(&self) -> f64 {
        if self.records.is_empty() {
            0.0
        } else {
            self.your_chests() as f64 / self.records.len() as f64
        }
    }

    pub fn add_match(&mut self, m: MatchChestCounts, fallback_time: DateTime<Utc>) -> bool {
        if m.match_id.is_empty() {
            return false;
        }
        let time = m.time.unwrap_or(fallback_time);
        if let Some(existing) = self.records.iter_mut().find(|r| r.match_id == m.match_id) {
            existing.you = m.you;
            existing.mates = m.mates;
            existing.prizes = m.prizes;
            return false;
        }
        self.records.push(MatchRecord {
            match_id: m.match_id,
            time,
            you: m.you,
            mates: m.mates,
            prizes: m.prizes,
        });
        true
    }

    pub fn records_since(&self, since: Option<DateTime<Utc>>) -> Vec<MatchRecord> {
        let mut recs: Vec<MatchRecord> = self
            .records
            .iter()
            .filter(|r| since.is_none_or(|s| r.time >= s))
            .cloned()
            .collect();
        recs.sort_by_key(|r| r.time);
        recs
    }

    pub fn over(&self, since: Option<DateTime<Utc>>) -> Period {
        let mut n = 0usize;
        let mut y = 0i64;
        for r in &self.records {
            if let Some(s) = since {
                if r.time < s {
                    continue;
                }
            }
            n += 1;
            y += r.you as i64;
        }
        Period {
            matches: n,
            your_chests: y,
            avg_you: if n > 0 { y as f64 / n as f64 } else { 0.0 },
        }
    }

    pub fn top_mates(&self, since: Option<DateTime<Utc>>, take: usize) -> Vec<MateTotal> {
        let mut map: HashMap<String, (usize, i64)> = HashMap::new();
        for r in &self.records {
            if let Some(s) = since {
                if r.time < s {
                    continue;
                }
            }
            for m in &r.mates {
                let entry = map.entry(m.name.clone()).or_insert((0, 0));
                entry.0 += 1;
                entry.1 += m.chests as i64;
            }
        }
        let mut list: Vec<MateTotal> = map
            .into_iter()
            .map(|(name, (matches, chests))| MateTotal {
                name,
                matches,
                chests,
            })
            .collect();
        list.sort_by(|a, b| {
            b.matches
                .cmp(&a.matches)
                .then_with(|| b.chests.cmp(&a.chests))
                .then_with(|| a.name.cmp(&b.name))
        });
        list.truncate(take);
        list
    }
}

pub fn app_data_dir() -> PathBuf {
    let base = dirs::data_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join("ChestCounter-Rust")
}

pub fn default_demos_path() -> PathBuf {
    let base = dirs::data_local_dir().unwrap_or_else(|| PathBuf::from("."));
    base.join("FortniteGame").join("Saved").join("Demos")
}

pub fn stats_path() -> PathBuf {
    app_data_dir().join("stats.json")
}

pub fn overlay_config_path() -> PathBuf {
    app_data_dir().join("overlay.json")
}

pub fn app_config_path() -> PathBuf {
    app_data_dir().join("app.json")
}

pub fn load_stats() -> LifetimeStats {
    load_stats_from(&stats_path())
}

pub fn load_stats_from(path: &PathBuf) -> LifetimeStats {
    if let Ok(content) = fs::read_to_string(path) {
        if let Ok(mut stats) = serde_json::from_str::<LifetimeStats>(&content) {
            if stats.version < CURRENT_VERSION {
                stats.scanned_files.clear();
                stats.version = CURRENT_VERSION;
            }
            return stats;
        }
    }
    LifetimeStats::default()
}

pub fn save_stats(stats: &LifetimeStats) {
    save_stats_to(&stats_path(), stats);
}

pub fn save_stats_to(path: &PathBuf, stats: &LifetimeStats) {
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let tmp = path.with_extension("json.tmp");
    if let Ok(json) = serde_json::to_string_pretty(stats) {
        if fs::write(&tmp, json).is_ok() {
            let _ = fs::rename(&tmp, path);
        }
        let _ = fs::remove_file(&tmp);
    }
}

pub fn overlay_json(stats: &LifetimeStats, live: Option<i32>) -> String {
    let all = stats.over(None);
    let mut best = 0;
    let mut latest: Option<&MatchRecord> = None;
    for r in &stats.records {
        if r.you > best {
            best = r.you;
        }
        if latest.is_none_or(|l| r.time >= l.time) {
            latest = Some(r);
        }
    }
    let last = latest.map(|l| l.you);
    serde_json::json!({
        "live": live,
        "avg": all.avg_you,
        "total": all.your_chests,
        "matches": all.matches,
        "best": best,
        "last": last,
        "domain": DOMAIN,
    })
    .to_string()
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct OverlaySettings {
    #[serde(rename = "Accent", default = "default_accent")]
    pub accent: String,
    #[serde(rename = "Title", default = "default_title")]
    pub title: String,
}

fn default_accent() -> String {
    DEFAULT_ACCENT.to_string()
}

fn default_title() -> String {
    "Bússola do Loot".to_string()
}

impl Default for OverlaySettings {
    fn default() -> Self {
        Self {
            accent: default_accent(),
            title: default_title(),
        }
    }
}

pub fn load_overlay_settings() -> OverlaySettings {
    let path = overlay_config_path();
    if let Ok(text) = fs::read_to_string(&path) {
        if let Ok(mut s) = serde_json::from_str::<OverlaySettings>(&text) {
            if s.accent == "#F5C147" || s.accent == "#E7B23C" {
                s.accent = DEFAULT_ACCENT.to_string();
            }
            if s.title == "Contador de baús" || s.title == "Chest Counter" || s.title == "Chest Tracker" {
                s.title = "Bússola do Loot".to_string();
            }
            return s;
        }
    }
    OverlaySettings::default()
}

pub fn save_overlay_settings(s: &OverlaySettings) {
    let path = overlay_config_path();
    if let Some(p) = path.parent() {
        let _ = fs::create_dir_all(p);
    }
    if let Ok(json) = serde_json::to_string_pretty(s) {
        let _ = fs::write(&path, json);
    }
}

pub fn overlay_config_json() -> String {
    let s = load_overlay_settings();
    let title = if s.title.trim().is_empty() {
        "Bússola do Loot"
    } else {
        &s.title
    };
    serde_json::json!({
        "accent": s.accent,
        "title": title,
        "domain": DOMAIN,
    })
    .to_string()
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppSettings {
    #[serde(rename = "InGameOverlay", default)]
    pub in_game_overlay: bool,
    #[serde(rename = "MinimizeToTray", default = "default_true")]
    pub minimize_to_tray: bool,
    #[serde(rename = "OverlayLeft")]
    pub overlay_left: Option<f64>,
    #[serde(rename = "OverlayTop")]
    pub overlay_top: Option<f64>,
}

fn default_true() -> bool {
    true
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            in_game_overlay: false,
            minimize_to_tray: true,
            overlay_left: None,
            overlay_top: None,
        }
    }
}

pub fn load_app_settings() -> AppSettings {
    let path = app_config_path();
    if let Ok(text) = fs::read_to_string(&path) {
        if let Ok(s) = serde_json::from_str::<AppSettings>(&text) {
            return s;
        }
    }
    AppSettings::default()
}

pub fn save_app_settings(s: &AppSettings) {
    let path = app_config_path();
    if let Some(p) = path.parent() {
        let _ = fs::create_dir_all(p);
    }
    if let Ok(json) = serde_json::to_string_pretty(s) {
        let _ = fs::write(&path, json);
    }
}
