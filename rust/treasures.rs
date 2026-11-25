//! Treasure-hunt odds, weekly prizes, Treasure Hours, and prize catalog for the Chapter 7 pirate season.

use chrono::{DateTime, Datelike, Duration, TimeZone, Utc, Weekday};
use std::collections::HashMap;
use std::sync::LazyLock;

pub const OUTFIT: &str = "outfit";
pub const TOKEN: &str = "token";
pub const CAR: &str = "car";
pub const PICKAXE: &str = "pickaxe";
pub const JAM: &str = "jam";
pub const LOADING: &str = "loading";
pub const GIZMO: &str = "gizmo";

pub const PIRATE_SHOP_TOKEN: &str = "9f73f94d-2e61-4523-97d7-d61235d3365e";
pub const GIZMO_PREFIX: &str = "gizmo:";

#[derive(Clone, Debug)]
pub struct TreasureOdds {
    pub kind: &'static str,
    pub label: &'static str,
    pub one_in: u64,
}

#[derive(Clone, Debug)]
pub struct TreasureWeek {
    pub number: u32,
    pub start_utc: DateTime<Utc>,
    pub end_utc: DateTime<Utc>,
    pub title: &'static str,
    pub prizes: HashMap<&'static str, &'static str>,
}

#[derive(Clone, Debug)]
pub struct TreasureHour {
    pub start_utc: DateTime<Utc>,
    pub end_utc: DateTime<Utc>,
    pub boosts: Vec<(&'static str, f64)>,
}

pub static ODDS: &[TreasureOdds] = &[
    TreasureOdds {
        kind: OUTFIT,
        label: "Traje dourado",
        one_in: 1_562_500,
    },
    TreasureOdds {
        kind: TOKEN,
        label: "Ficha da Loja Pirata",
        one_in: 137_500,
    },
    TreasureOdds {
        kind: CAR,
        label: "Adesivo de veículo",
        one_in: 7_813,
    },
    TreasureOdds {
        kind: PICKAXE,
        label: "Picareta / acessório para as costas",
        one_in: 568,
    },
    TreasureOdds {
        kind: JAM,
        label: "Música de improviso",
        one_in: 391,
    },
    TreasureOdds {
        kind: LOADING,
        label: "Tela de carregamento",
        one_in: 272,
    },
    TreasureOdds {
        kind: GIZMO,
        label: "Engenhoca",
        one_in: 127,
    },
];

fn utc(month: u32, day: u32, hour: u32) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, month, day, hour, 0, 0).unwrap()
}

pub static WEEKS: LazyLock<Vec<TreasureWeek>> = LazyLock::new(|| {
    vec![
        TreasureWeek {
            number: 1,
            start_utc: utc(11, 1, 14),
            end_utc: utc(11, 8, 14),
            title: "A$AP Arrives",
            prizes: HashMap::from([
                (OUTFIT, "Solid Gold A$AP Rocky"),
                (PICKAXE, "Solid Gold A$AP Rocky's Gauntlets"),
                (CAR, "Gold Mercedes-AMG G 63 Decal"),
                (JAM, "No Trespassing"),
                (LOADING, "Plunder in Paradise"),
            ]),
        },
        TreasureWeek {
            number: 2,
            start_utc: utc(11, 8, 14),
            end_utc: utc(11, 15, 14),
            title: "Lil Uzi Arrives",
            prizes: HashMap::from([
                (OUTFIT, "Solid Gold Lil Uzi Vert"),
                (PICKAXE, "Diamond and Gold"),
                (CAR, "Gold Cadillac CT5-V Decal"),
                (JAM, "London"),
                (LOADING, "So Kawaii"),
            ]),
        },
        TreasureWeek {
            number: 3,
            start_utc: utc(11, 15, 14),
            end_utc: utc(11, 22, 14),
            title: "Miku & Teto Arrive",
            prizes: HashMap::new(),
        },
        TreasureWeek {
            number: 4,
            start_utc: utc(11, 22, 14),
            end_utc: utc(11, 29, 14),
            title: "Oscar Arrives",
            prizes: HashMap::new(),
        },
        TreasureWeek {
            number: 5,
            start_utc: utc(11, 29, 14),
            end_utc: utc(12, 5, 14),
            title: "The Final Hunt",
            prizes: HashMap::new(),
        },
    ]
});

pub fn season_start_utc() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 11, 1, 0, 0, 0).unwrap()
}

pub fn season_end_utc() -> DateTime<Utc> {
    WEEKS.last().unwrap().end_utc
}

pub fn week_at(now: DateTime<Utc>) -> Option<&'static TreasureWeek> {
    WEEKS.iter().find(|w| now >= w.start_utc && now < w.end_utc)
}

pub fn current_or_next_hour(now: DateTime<Utc>) -> Option<TreasureHour> {
    let end_season = season_end_utc();
    let mut day = Utc
        .with_ymd_and_hms(now.year(), now.month(), now.day(), 0, 0, 0)
        .unwrap()
        - Duration::days(1);

    while day < end_season {
        let hour = match day.weekday() {
            Weekday::Sat => Some(19),
            Weekday::Sun => Some(2),
            _ => None,
        };
        if let Some(h) = hour {
            let start = day + Duration::hours(h);
            let end = start + Duration::hours(2);
            if end > now && start < end_season {
                return Some(TreasureHour {
                    start_utc: start,
                    end_utc: end,
                    boosts: vec![(OUTFIT, 7.5), (TOKEN, 1.5), (CAR, 10.0)],
                });
            }
        }
        day += Duration::days(1);
    }
    None
}

pub fn is_hour_live(hour: &TreasureHour, now: DateTime<Utc>) -> bool {
    now >= hour.start_utc && now < hour.end_utc
}

pub fn chance_of_at_least_one(one_in: u64, chests: i64) -> f64 {
    if chests <= 0 || one_in == 0 {
        0.0
    } else {
        1.0 - (1.0 - 1.0 / (one_in as f64)).powf(chests as f64)
    }
}

pub fn is_gizmo(prize_id: &str) -> bool {
    prize_id.starts_with(GIZMO_PREFIX)
}

pub fn is_token(prize_id: &str) -> bool {
    prize_id.eq_ignore_ascii_case(PIRATE_SHOP_TOKEN)
}

pub fn prize_name(prize_id: &str) -> String {
    if is_gizmo(prize_id) {
        let item = &prize_id[GIZMO_PREFIX.len()..];
        if item.is_empty() {
            return "Engenhoca".to_string();
        }
        let cleaned = item
            .replace("WID_", "")
            .replace("_Gizmo", "")
            .replace("_Consumable_S43", "");
        let mut spaced = String::new();
        let chars: Vec<char> = cleaned.chars().collect();
        for i in 0..chars.len() {
            if i > 0 && chars[i - 1].is_ascii_lowercase() && chars[i].is_ascii_uppercase() {
                spaced.push(' ');
            }
            spaced.push(chars[i]);
        }
        return format!("Engenhoca: {}", spaced);
    }

    let lower = prize_id.to_ascii_lowercase();
    match lower.as_str() {
        "9f73f94d-2e61-4523-97d7-d61235d3365e" => "Ficha de acesso à Loja Pirata".to_string(),
        "delilahprize:dpid_item1" => "Solid Gold A$AP Rocky (traje)".to_string(),
        "delilahprize:dpid_item2" => "Solid Gold A$AP Rocky's Gauntlets".to_string(),
        "delilahprize:dpid_item3" => "No Trespassing (música de improviso)".to_string(),
        "delilahprize:dpid_item4" => "Plunder in Paradise (tela de carregamento)".to_string(),
        "delilahprize:dpid_item5" => "Gold Mercedes-AMG G 63 Decal".to_string(),
        "delilahprize:dpid_item14" => "Solid Gold Lil Uzi Vert (traje)".to_string(),
        "delilahprize:dpid_item15" => "Diamond and Gold (acessório para as costas)".to_string(),
        "delilahprize:dpid_item16" => "London (música de improviso)".to_string(),
        "delilahprize:dpid_item17" => "So Kawaii (tela de carregamento)".to_string(),
        "delilahprize:dpid_item18" => "Gold Cadillac CT5-V Decal".to_string(),
        _ => format!("Tesouro ({})", prize_id),
    }
}
