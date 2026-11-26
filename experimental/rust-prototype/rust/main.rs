#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! Bússola do Loot do Fortnite — Native Rust GUI + Live OCR + Multi-core Replay Scanner + OBS & In-Game Overlay.

use chest_counter::ocr::LiveChestCounter;
use chest_counter::overlay_server::OverlayServer;
use chest_counter::replay::{find_replays, scan_replays};
use chest_counter::stats::{
    AppSettings, DOMAIN, LifetimeStats, MatchRecord, OverlaySettings, PrizeFind,
    default_demos_path, load_app_settings, load_overlay_settings, load_stats, save_app_settings,
    save_overlay_settings, save_stats,
};
use chest_counter::treasures::{
    self, ODDS, chance_of_at_least_one, current_or_next_hour, is_gizmo, is_hour_live, is_token,
    prize_name, season_start_utc, week_at,
};
use chrono::{DateTime, Duration as ChronoDuration, Local, Utc};
use eframe::egui::{
    self, Align, Color32, CornerRadius, CursorIcon, FontData, FontDefinitions, FontFamily, FontId,
    Frame, Id, Layout, Margin, Pos2, Rect, RichText, ScrollArea, Sense, Stroke, TextureHandle,
    Vec2, ViewportBuilder, ViewportCommand, ViewportId,
};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, Ordering},
};
use std::thread;
use std::time::{Duration, Instant};

// ---- Palette matching the dark bone/steel/gem theme ----
const BG: Color32 = Color32::from_rgb(0x07, 0x07, 0x08);
const CARD: Color32 = Color32::from_rgb(0x11, 0x11, 0x13);
const CARD_ALT: Color32 = Color32::from_rgb(0x1B, 0x1B, 0x1F);
const STROKE: Color32 = Color32::from_rgb(0x24, 0x24, 0x28);
const TEXT_COLOR: Color32 = Color32::from_rgb(0xF3, 0xF1, 0xEC);
const MUTED: Color32 = Color32::from_rgb(0x9A, 0x9A, 0xA3);
const STEEL: Color32 = Color32::from_rgb(0xC9, 0xD2, 0xDA);
const GEM: Color32 = Color32::from_rgb(0xFF, 0x3D, 0x6B);
const INK: Color32 = Color32::from_rgb(0x05, 0x05, 0x05);

const PRESET_COLORS: &[(&str, Color32)] = &[
    ("#F3F1EC", Color32::from_rgb(0xF3, 0xF1, 0xEC)),
    ("#FF3D6B", Color32::from_rgb(0xFF, 0x3D, 0x6B)),
    ("#3DDC84", Color32::from_rgb(0x3D, 0xDC, 0x84)),
    ("#A78BFA", Color32::from_rgb(0xA7, 0x8B, 0xFA)),
    ("#FF8A3D", Color32::from_rgb(0xFF, 0x8A, 0x3D)),
    ("#4CC9F0", Color32::from_rgb(0x4C, 0xC9, 0xF0)),
];

fn parse_hex_color(hex: &str) -> Option<Color32> {
    let s = hex.trim().strip_prefix('#').unwrap_or(hex.trim());
    if s.len() == 6 {
        let r = u8::from_str_radix(&s[0..2], 16).ok()?;
        let g = u8::from_str_radix(&s[2..4], 16).ok()?;
        let b = u8::from_str_radix(&s[4..6], 16).ok()?;
        Some(Color32::from_rgb(r, g, b))
    } else {
        None
    }
}

fn format_thousands(n: i64) -> String {
    let s = n.abs().to_string();
    let mut out = String::new();
    for (count, ch) in s.chars().rev().enumerate() {
        if count > 0 && count % 3 == 0 {
            out.push('.');
        }
        out.push(ch);
    }
    if n < 0 {
        out.push('-');
    }
    out.chars().rev().collect()
}

fn format_decimal_pt(val: f64, decimals: usize) -> String {
    format!("{:.prec$}", val, prec = decimals).replace('.', ",")
}

fn finder_label(p: &PrizeFind) -> String {
    if is_gizmo(&p.prize_id) {
        return match p.by.as_str() {
            "" => "seu esquadrão".to_string(),
            "you" => "seu esquadrão, no seu baú".to_string(),
            other => format!("seu esquadrão, no baú de {}", other),
        };
    }
    if p.mine {
        "você".to_string()
    } else if p.team {
        if p.by.is_empty() {
            "seu esquadrão".to_string()
        } else {
            format!("{} (esquadrão)", p.by)
        }
    } else if p.by.is_empty() {
        "alguém por perto".to_string()
    } else {
        p.by.clone()
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum TimeFilter {
    Hours24 = 0,
    Days7 = 1,
    Season = 2,
    All = 3,
}

struct ChestCounterApp {
    stats: Arc<Mutex<LifetimeStats>>,
    overlay_settings: OverlaySettings,
    app_settings: AppSettings,
    #[allow(dead_code)]
    server: Option<OverlayServer>,
    live_counter: Arc<LiveChestCounter>,
    live_enabled: bool,
    filter: TimeFilter,
    chest_texture: Option<TextureHandle>,
    status_text: Arc<Mutex<String>>,
    overlay_status_text: String,
    scanning: Arc<AtomicBool>,
    confirm_reset: bool,
    overlay_locked: Arc<AtomicBool>,
    toggle_overlay_req: Arc<AtomicBool>,
    lock_overlay_req: Arc<AtomicBool>,
    last_seen_popup: String,
    last_seen_log: String,
    first_frame: bool,
}

impl ChestCounterApp {
    fn new(cc: &eframe::CreationContext<'_>) -> Self {
        setup_fonts_and_style(&cc.egui_ctx);

        let initial_stats = load_stats();
        let demos_exists = default_demos_path().exists();

        // Auto-import replays on first launch if stats are empty and Demos folder has replays!
        let initial_files = find_replays(None);
        let auto_scan_on_boot = initial_stats.records.is_empty() && !initial_files.is_empty();

        let stats = Arc::new(Mutex::new(initial_stats));
        let overlay_settings = load_overlay_settings();
        let app_settings = load_app_settings();

        let live_counter = Arc::new(LiveChestCounter::new());
        let server = OverlayServer::start(7777, stats.clone(), live_counter.live_raw.clone());
        let overlay_status_text = if server.is_some() {
            "No OBS, adicione o endereço acima como fonte do tipo Navegador.".to_string()
        } else {
            "O endereço do OBS já está em uso ou indisponível.".to_string()
        };

        let mut status = if !demos_exists {
            "Pasta de replays ausente. Ative a gravação de replays no Fortnite.".to_string()
        } else if live_counter.available {
            format!(
                "OCR {} ligado. Jogue em janela sem bordas.",
                live_counter.language_tag
            )
        } else {
            "Instale o OCR nas opções de idioma do Windows.".to_string()
        };

        let status_text = Arc::new(Mutex::new(status.clone()));
        let scanning = Arc::new(AtomicBool::new(false));

        let live_enabled = live_counter.available;
        if live_enabled {
            let ctx = cc.egui_ctx.clone();
            live_counter.start(
                stats.clone(),
                Arc::new(move || {
                    ctx.request_repaint();
                }),
            );
        }

        // Load chest.png texture
        let chest_bytes = include_bytes!("../src/Assets/chest.png");
        let chest_texture = image::load_from_memory(chest_bytes).ok().map(|img| {
            let rgba = img.to_rgba8();
            let size = [rgba.width() as usize, rgba.height() as usize];
            let color_img = egui::ColorImage::from_rgba_unmultiplied(size, rgba.as_raw());
            cc.egui_ctx
                .load_texture("chest_hero", color_img, egui::TextureOptions::LINEAR)
        });

        let toggle_overlay_req = Arc::new(AtomicBool::new(false));
        let lock_overlay_req = Arc::new(AtomicBool::new(false));
        let overlay_locked = Arc::new(AtomicBool::new(false));

        // Global Hotkey Thread (Ctrl+Shift+O and Ctrl+Shift+L)
        #[cfg(windows)]
        {
            let toggle_req = toggle_overlay_req.clone();
            let lock_req = lock_overlay_req.clone();
            let ctx = cc.egui_ctx.clone();
            thread::spawn(move || {
                use windows::Win32::Foundation::HWND;
                use windows::Win32::UI::Input::KeyboardAndMouse::{
                    MOD_CONTROL, MOD_NOREPEAT, MOD_SHIFT, RegisterHotKey,
                };
                use windows::Win32::UI::WindowsAndMessaging::{GetMessageW, MSG, WM_HOTKEY};
                unsafe {
                    let mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
                    let _ = RegisterHotKey(HWND(std::ptr::null_mut()), 9001, mods, 0x4F); // 'O'
                    let _ = RegisterHotKey(HWND(std::ptr::null_mut()), 9002, mods, 0x4C); // 'L'
                    let mut msg = MSG::default();
                    while GetMessageW(&mut msg, HWND(std::ptr::null_mut()), 0, 0).as_bool() {
                        if msg.message == WM_HOTKEY {
                            match msg.wParam.0 {
                                9001 => {
                                    toggle_req.store(true, Ordering::Relaxed);
                                    ctx.request_repaint();
                                }
                                9002 => {
                                    lock_req.store(true, Ordering::Relaxed);
                                    ctx.request_repaint();
                                }
                                _ => {}
                            }
                        }
                    }
                }
            });
        }

        let app = Self {
            stats,
            overlay_settings,
            app_settings,
            server,
            live_counter,
            live_enabled,
            filter: TimeFilter::Season,
            chest_texture,
            status_text,
            overlay_status_text,
            scanning,
            confirm_reset: false,
            overlay_locked,
            toggle_overlay_req,
            lock_overlay_req,
            last_seen_popup: String::new(),
            last_seen_log: String::new(),
            first_frame: true,
        };

        if auto_scan_on_boot {
            app.trigger_scan(&cc.egui_ctx);
        }

        let _ = &mut status;
        app
    }

    fn trigger_scan(&self, ctx: &egui::Context) {
        if self.scanning.load(Ordering::Relaxed) {
            return;
        }
        let files = find_replays(None);
        if files.is_empty() {
            if let Ok(mut st) = self.status_text.lock() {
                *st = "Nenhum arquivo .replay encontrado. Confira a gravação de replays no Fortnite."
                    .to_string();
            }
            return;
        }

        self.scanning.store(true, Ordering::Relaxed);
        if let Ok(mut st) = self.status_text.lock() {
            *st = format!("Lendo 0/{}...", files.len());
        }

        let stats_arc = self.stats.clone();
        let status_arc = self.status_text.clone();
        let scanning_arc = self.scanning.clone();
        let ctx_clone = ctx.clone();

        thread::spawn(move || {
            let t0 = Instant::now();
            let mut current_stats = stats_arc
                .lock()
                .map(|s| s.clone())
                .unwrap_or_default();

            let status_progress = status_arc.clone();
            let ctx_progress = ctx_clone.clone();
            let run = scan_replays(
                &files,
                &mut current_stats,
                Some(move |done, total, _name: &str, _err: Option<&str>| {
                    if done % 3 == 0 || done == total {
                        if let Ok(mut st) = status_progress.lock() {
                            *st = format!("Lendo {}/{}...", done, total);
                        }
                        ctx_progress.request_repaint();
                    }
                }),
            );

            if run.parsed > 0 {
                save_stats(&current_stats);
            }
            if let Ok(mut locked) = stats_arc.lock() {
                *locked = current_stats;
            }

            let skipped = if run.skipped_mode > 0 {
                format!(", {} fora dos modos aceitos", run.skipped_mode)
            } else {
                String::new()
            };
            if let Ok(mut st) = status_arc.lock() {
                *st = format!(
                    "Leitura concluída em {:.1}s. Novas: {} · já contadas: {}{} · falhas: {}.",
                    t0.elapsed().as_secs_f64(),
                    run.new_matches,
                    run.already_counted,
                    skipped,
                    run.failed
                );
            }
            scanning_arc.store(false, Ordering::Relaxed);
            ctx_clone.request_repaint();
        });
    }

    fn selected_since(&self) -> Option<DateTime<Utc>> {
        match self.filter {
            TimeFilter::Hours24 => Some(Utc::now() - ChronoDuration::days(1)),
            TimeFilter::Days7 => Some(Utc::now() - ChronoDuration::days(7)),
            TimeFilter::Season => Some(season_start_utc()),
            TimeFilter::All => None,
        }
    }
}

fn setup_fonts_and_style(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();

    // Load native Windows fonts (Bahnschrift for Display numbers, Segoe UI for clean Portuguese text)
    if let Ok(segoe) = std::fs::read("C:\\Windows\\Fonts\\segoeui.ttf") {
        fonts
            .font_data
            .insert("segoe".to_owned(), Arc::new(FontData::from_owned(segoe)));
        fonts
            .families
            .entry(FontFamily::Proportional)
            .or_default()
            .insert(0, "segoe".to_owned());
    }
    if let Ok(segoeb) = std::fs::read("C:\\Windows\\Fonts\\segoeuib.ttf") {
        fonts
            .font_data
            .insert("segoeb".to_owned(), Arc::new(FontData::from_owned(segoeb)));
    }
    if let Ok(bahnschrift) = std::fs::read("C:\\Windows\\Fonts\\bahnschrift.ttf") {
        fonts.font_data.insert(
            "display".to_owned(),
            Arc::new(FontData::from_owned(bahnschrift)),
        );
        fonts
            .families
            .insert(FontFamily::Name("display".into()), vec!["display".to_owned(), "segoe".to_owned()]);
    } else {
        fonts
            .families
            .insert(FontFamily::Name("display".into()), vec!["segoe".to_owned()]);
    }

    ctx.set_fonts(fonts);

    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = BG;
    visuals.window_fill = CARD;
    visuals.override_text_color = Some(TEXT_COLOR);
    visuals.widgets.noninteractive.bg_fill = CARD;
    visuals.widgets.noninteractive.bg_stroke = Stroke::new(1.0_f32, STROKE);
    visuals.widgets.noninteractive.fg_stroke = Stroke::new(1.0_f32, TEXT_COLOR);
    visuals.widgets.inactive.bg_fill = CARD_ALT;
    visuals.widgets.inactive.bg_stroke = Stroke::new(1.0_f32, STROKE);
    visuals.widgets.inactive.fg_stroke = Stroke::new(1.0_f32, TEXT_COLOR);
    visuals.widgets.hovered.bg_fill = Color32::from_rgb(0x28, 0x28, 0x2E);
    visuals.widgets.hovered.bg_stroke = Stroke::new(1.0_f32, TEXT_COLOR);
    visuals.widgets.active.bg_fill = TEXT_COLOR;
    visuals.widgets.active.fg_stroke = Stroke::new(1.0_f32, INK);
    ctx.set_visuals(visuals);
}

fn display_font(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name("display".into()))
}

fn body_font(size: f32) -> FontId {
    FontId::new(size, FontFamily::Proportional)
}

#[cfg(windows)]
fn apply_dark_title_bar() {
    use windows::Win32::Graphics::Dwm::{DWMWA_USE_IMMERSIVE_DARK_MODE, DwmSetWindowAttribute};
    use windows::Win32::UI::Input::KeyboardAndMouse::GetActiveWindow;
    unsafe {
        let hwnd = GetActiveWindow();
        if !hwnd.0.is_null() {
            let on: i32 = 1;
            let _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                &on as *const _ as *const _,
                std::mem::size_of::<i32>() as u32,
            );
        }
    }
}

impl eframe::App for ChestCounterApp {
    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        [0.0, 0.0, 0.0, 0.0]
    }

    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if self.first_frame {
            self.first_frame = false;
            #[cfg(windows)]
            apply_dark_title_bar();
        }

        // Request periodic repaint every second for the Treasure Hour live countdown
        ctx.request_repaint_after(Duration::from_secs(1));

        // Handle global hotkeys
        if self.toggle_overlay_req.swap(false, Ordering::Relaxed) {
            self.app_settings.in_game_overlay = !self.app_settings.in_game_overlay;
            save_app_settings(&self.app_settings);
        }
        if self.lock_overlay_req.swap(false, Ordering::Relaxed) {
            let cur = self.overlay_locked.load(Ordering::Relaxed);
            self.overlay_locked.store(!cur, Ordering::Relaxed);
        }

        // Sync live OCR popup/log messages to status bar
        if let Ok(popup) = self.live_counter.last_popup_text.lock() {
            if !popup.is_empty() && *popup != self.last_seen_popup {
                self.last_seen_popup = popup.clone();
                if let Ok(mut st) = self.status_text.lock() {
                    *st = format!("Último aviso de EXP lido: “{}”", self.last_seen_popup);
                }
            }
        }
        if let Ok(lg) = self.live_counter.last_log_text.lock() {
            if !lg.is_empty() && *lg != self.last_seen_log {
                self.last_seen_log = lg.clone();
                if let Ok(mut st) = self.status_text.lock() {
                    *st = self.last_seen_log.clone();
                }
            }
        }

        let live_raw = self.live_counter.live_raw.load(Ordering::Relaxed);
        let live_count = if live_raw < 0 { None } else { Some(live_raw) };

        let since = self.selected_since();
        let (recs, season_chests, top_mates, all_avg, last_match_you) = {
            let st = self.stats.lock().unwrap();
            let recs = st.records_since(since);
            let season_chests = st.over(Some(season_start_utc())).your_chests;
            let top_mates = st.top_mates(since, 4);
            let all_avg = st.avg_you();
            let all_recs = st.records_since(None);
            let last_you = all_recs.last().map(|r| r.you);
            (recs, season_chests, top_mates, all_avg, last_you)
        };

        // Render In-Game Overlay Viewport if active
        if self.app_settings.in_game_overlay {
            self.render_in_game_overlay(ctx, live_count, all_avg, last_match_you);
        }

        egui::CentralPanel::default()
            .frame(
                Frame::NONE
                    .fill(BG)
                    .inner_margin(Margin {
                        left: 32,
                        right: 32,
                        top: 24,
                        bottom: 20,
                    }),
            )
            .show(ctx, |ui| {
                // ---- HEADER ----
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("Bússola do Loot")
                            .font(display_font(30.0))
                            .color(TEXT_COLOR)
                            .strong(),
                    );

                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        let is_scanning = self.scanning.load(Ordering::Relaxed);

                        // Primary Button: Ler replays
                        let scan_btn = egui::Button::new(
                            RichText::new(if is_scanning { "Lendo..." } else { "Ler replays" })
                                .font(body_font(13.0))
                                .color( if is_scanning { MUTED } else { INK } )
                                .strong(),
                        )
                        .fill(if is_scanning { CARD_ALT } else { TEXT_COLOR })
                        .stroke(Stroke::new(1.0_f32, if is_scanning { STROKE } else { TEXT_COLOR }))
                        .corner_radius(CornerRadius::same(7))
                        .min_size(Vec2::new(105.0, 34.0));

                        if ui.add_enabled(!is_scanning, scan_btn).clicked() {
                            self.trigger_scan(ctx);
                        }

                        ui.add_space(6.0);

                        // Quiet Button: Ao vivo ligado / pausado
                        let live_label = if !self.live_counter.available {
                            "OCR indisponível"
                        } else if self.live_enabled {
                            "● Ao vivo ligado"
                        } else {
                            "Ao vivo pausado"
                        };
                        let live_color = if self.live_enabled { GEM } else { TEXT_COLOR };
                        let live_btn = egui::Button::new(
                            RichText::new(live_label)
                                .font(body_font(12.5))
                                .color(live_color),
                        )
                        .fill(Color32::TRANSPARENT)
                        .stroke(Stroke::new(1.0_f32, STROKE))
                        .corner_radius(CornerRadius::same(7))
                        .min_size(Vec2::new(124.0, 34.0));

                        if ui
                            .add_enabled(self.live_counter.available, live_btn)
                            .clicked()
                        {
                            if self.live_enabled {
                                self.live_counter.stop();
                                self.live_enabled = false;
                            } else {
                                let ctx_c = ctx.clone();
                                self.live_counter.start(
                                    self.stats.clone(),
                                    Arc::new(move || ctx_c.request_repaint()),
                                );
                                self.live_enabled = true;
                            }
                        }

                        ui.add_space(6.0);

                        // Quiet Button: Zerar
                        let reset_btn = egui::Button::new(
                            RichText::new("Zerar")
                                .font(body_font(12.5))
                                .color(TEXT_COLOR),
                        )
                        .fill(Color32::TRANSPARENT)
                        .stroke(Stroke::new(1.0_f32, STROKE))
                        .corner_radius(CornerRadius::same(7))
                        .min_size(Vec2::new(68.0, 34.0));

                        if ui
                            .add_enabled(!is_scanning, reset_btn)
                            .on_hover_text("Apagar todas as partidas salvas")
                            .clicked()
                        {
                            self.confirm_reset = true;
                        }

                        ui.add_space(12.0);

                        // Time Filter Pills
                        Frame::NONE
                            .fill(CARD)
                            .stroke(Stroke::new(1.0_f32, STROKE))
                            .corner_radius(CornerRadius::same(9))
                            .inner_margin(Margin::same(3))
                            .show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    ui.spacing_mut().item_spacing.x = 2.0;
                                    for (flt, label) in [
                                        (TimeFilter::Hours24, "24 horas"),
                                        (TimeFilter::Days7, "7 dias"),
                                        (TimeFilter::Season, "Temporada"),
                                        (TimeFilter::All, "Tudo"),
                                    ] {
                                        let active = self.filter == flt;
                                        let pill = egui::Button::new(
                                            RichText::new(label)
                                                .font(body_font(12.5))
                                                .color(if active { TEXT_COLOR } else { MUTED }),
                                        )
                                        .fill(if active {
                                            CARD_ALT
                                        } else {
                                            Color32::TRANSPARENT
                                        })
                                        .stroke(Stroke::NONE)
                                        .corner_radius(CornerRadius::same(6))
                                        .min_size(Vec2::new(68.0, 28.0));

                                        if ui.add(pill).clicked() {
                                            self.filter = flt;
                                        }
                                    }
                                });
                            });
                    });
                });

                ui.add_space(18.0);

                // ---- FOOTER (reserved at bottom) ----
                egui::TopBottomPanel::bottom("footer_panel")
                    .frame(Frame::NONE.fill(BG).inner_margin(Margin {
                        left: 2,
                        right: 2,
                        top: 12,
                        bottom: 0,
                    }))
                    .show_inside(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(DOMAIN)
                                    .font(body_font(11.5))
                                    .color(MUTED),
                            );
                            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                let st_msg = self
                                    .status_text
                                    .lock()
                                    .map(|s| s.clone())
                                    .unwrap_or_default();
                                ui.label(
                                    RichText::new(st_msg)
                                        .font(body_font(12.5))
                                        .color(MUTED),
                                );
                            });
                        });
                    });

                // ---- MAIN 2-COLUMN BODY ----
                let avail = ui.available_size();
                let right_width = 392.0f32.min(avail.x * 0.36);
                let left_width = (avail.x - right_width - 16.0).max(400.0);

                ui.horizontal_top(|ui| {
                    ui.spacing_mut().item_spacing.x = 16.0;

                    // ==== LEFT COLUMN ====
                    ui.allocate_ui_with_layout(
                        Vec2::new(left_width, avail.y),
                        Layout::top_down(Align::Min),
                        |ui| {
                            self.render_left_column(ui, &recs, &top_mates, live_count);
                        },
                    );

                    // ==== RIGHT COLUMN ====
                    ui.allocate_ui_with_layout(
                        Vec2::new(right_width, avail.y),
                        Layout::top_down(Align::Min),
                        |ui| {
                            ScrollArea::vertical()
                                .id_salt("right_scroll")
                                .show(ui, |ui| {
                                    self.render_right_column(ui, &recs, season_chests);
                                });
                        },
                    );
                });
            });

        // Reset confirmation modal
        if self.confirm_reset {
            egui::Window::new("Zerar estatísticas")
                .collapsible(false)
                .resizable(false)
                .anchor(egui::Align2::CENTER_CENTER, Vec2::ZERO)
                .frame(
                    Frame::NONE
                        .fill(CARD)
                        .stroke(Stroke::new(1.0_f32, GEM))
                        .corner_radius(CornerRadius::same(12))
                        .inner_margin(Margin::same(24)),
                )
                .show(ctx, |ui| {
                    ui.set_max_width(380.0);
                    ui.label(
                        RichText::new("Apagar todas as partidas salvas e começar de novo?")
                            .font(body_font(15.0))
                            .color(TEXT_COLOR)
                            .strong(),
                    );
                    ui.add_space(8.0);
                    ui.label(
                        RichText::new(
                            "Os replays que ainda estão na pasta do Fortnite podem ser lidos novamente. As partidas sem replay serão perdidas.",
                        )
                        .font(body_font(13.0))
                        .color(MUTED),
                    );
                    ui.add_space(18.0);
                    ui.horizontal(|ui| {
                        let ok_btn = egui::Button::new(
                            RichText::new("Sim, zerar")
                                .font(body_font(13.0))
                                .color(INK)
                                .strong(),
                        )
                        .fill(GEM)
                        .corner_radius(CornerRadius::same(7))
                        .min_size(Vec2::new(110.0, 34.0));

                        if ui.add(ok_btn).clicked() {
                            let fresh = LifetimeStats::default();
                            save_stats(&fresh);
                            if let Ok(mut st) = self.stats.lock() {
                                *st = fresh;
                            }
                            if let Ok(mut st) = self.status_text.lock() {
                                *st = "Estatísticas zeradas. Clique em Ler replays para importar as partidas.".to_string();
                            }
                            self.confirm_reset = false;
                        }

                        ui.add_space(10.0);

                        let cancel_btn = egui::Button::new(
                            RichText::new("Cancelar")
                                .font(body_font(13.0))
                                .color(TEXT_COLOR),
                        )
                        .fill(CARD_ALT)
                        .stroke(Stroke::new(1.0_f32, STROKE))
                        .corner_radius(CornerRadius::same(7))
                        .min_size(Vec2::new(100.0, 34.0));

                        if ui.add(cancel_btn).clicked() {
                            self.confirm_reset = false;
                        }
                    });
                });
        }
    }
}

impl ChestCounterApp {
    fn render_left_column(
        &self,
        ui: &mut egui::Ui,
        recs: &[MatchRecord],
        top_mates: &[chest_counter::stats::MateTotal],
        live_count: Option<i32>,
    ) {
        let n = recs.len();
        let total: i64 = recs.iter().map(|r| r.you as i64).sum();
        let avg = if n > 0 { total as f64 / n as f64 } else { 0.0 };
        let best = recs.iter().max_by_key(|r| r.you);
        let last10 = if n > 10 { &recs[n - 10..] } else { recs };
        let recent_avg = if !last10.is_empty() {
            Some(last10.iter().map(|r| r.you as f64).sum::<f64>() / last10.len() as f64)
        } else {
            None
        };
        let last_rec = recs.last();

        // ---- 1. HERO PANEL ----
        Frame::NONE
            .fill(CARD)
            .stroke(Stroke::new(1.0_f32, STROKE))
            .corner_radius(CornerRadius::same(14))
            .show(ui, |ui| {
                ui.set_width(ui.available_width());

                // Top part of Hero: Live count + Chest image + subtle crimson glow
                let (hero_rect, _) =
                    ui.allocate_exact_size(Vec2::new(ui.available_width(), 178.0), Sense::hover());

                let painter = ui.painter_at(hero_rect);
                // Subtle radial crimson glow behind the chest on the right
                let glow_center = Pos2::new(hero_rect.right() - 115.0, hero_rect.center().y);
                for step in (1..=12).rev() {
                    let radius = (step as f32) * 18.0;
                    let alpha = (13 - step) as u8 * 3;
                    painter.circle_filled(
                        glow_center,
                        radius,
                        Color32::from_rgba_unmultiplied(0xFF, 0x3D, 0x6B, alpha),
                    );
                }

                // Chest illustration on the right
                if let Some(ref tex) = self.chest_texture {
                    let img_size = Vec2::new(148.0, 148.0);
                    let img_rect = Rect::from_min_size(
                        Pos2::new(
                            hero_rect.right() - img_size.x - 36.0,
                            hero_rect.center().y - img_size.y * 0.5,
                        ),
                        img_size,
                    );
                    painter.image(
                        tex.id(),
                        img_rect,
                        Rect::from_min_max(Pos2::ZERO, Pos2::new(1.0, 1.0)),
                        Color32::WHITE,
                    );
                }

                // Live Match Count on the left
                let left_x = hero_rect.left() + 30.0;
                let dot_pos = Pos2::new(left_x + 4.0, hero_rect.top() + 28.0);
                painter.circle_filled(dot_pos, 4.0, GEM);
                painter.text(
                    Pos2::new(left_x + 16.0, hero_rect.top() + 19.0),
                    egui::Align2::LEFT_TOP,
                    "Nesta partida",
                    body_font(13.5),
                    TEXT_COLOR,
                );

                let live_str = live_count
                    .map(|c| c.to_string())
                    .unwrap_or_else(|| "—".to_string());
                let accent_color =
                    parse_hex_color(&self.overlay_settings.accent).unwrap_or(TEXT_COLOR);
                painter.text(
                    Pos2::new(left_x - 2.0, hero_rect.top() + 38.0),
                    egui::Align2::LEFT_TOP,
                    &live_str,
                    display_font(88.0),
                    accent_color,
                );

                let sub_str = if live_count.is_none() {
                    "Aguardando partida…"
                } else {
                    "baús abertos ao vivo"
                };
                painter.text(
                    Pos2::new(left_x, hero_rect.bottom() - 28.0),
                    egui::Align2::LEFT_TOP,
                    sub_str,
                    body_font(13.0),
                    MUTED,
                );

                // Divider line
                let (div_rect, _) =
                    ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), Sense::hover());
                ui.painter().rect_filled(div_rect, 0.0, STROKE);

                // Bottom 4-column summary strip
                let strip_h = 88.0;
                let (strip_rect, _) = ui
                    .allocate_exact_size(Vec2::new(ui.available_width(), strip_h), Sense::hover());
                let sp = ui.painter_at(strip_rect);
                sp.rect_filled(
                    strip_rect,
                    CornerRadius {
                        nw: 0,
                        ne: 0,
                        sw: 14,
                        se: 14,
                    },
                    Color32::from_rgba_unmultiplied(255, 255, 255, 6),
                );

                let col_w = strip_rect.width() / 4.0;
                let cols = [
                    (
                        "Média",
                        format_decimal_pt(avg, 1),
                        recent_avg
                            .map(|r| format!("média recente: {}", format_decimal_pt(r, 1)))
                            .unwrap_or_default(),
                        TEXT_COLOR,
                    ),
                    (
                        "Baús abertos",
                        format_thousands(total),
                        if n == 1 {
                            "em 1 partida".to_string()
                        } else {
                            format!("em {} partidas", format_thousands(n as i64))
                        },
                        TEXT_COLOR,
                    ),
                    (
                        "Última partida",
                        last_rec
                            .map(|r| r.you.to_string())
                            .unwrap_or_else(|| "—".to_string()),
                        last_rec
                            .map(|r| {
                                r.time
                                    .with_timezone(&Local)
                                    .format("%d/%m, %H:%M")
                                    .to_string()
                            })
                            .unwrap_or_default(),
                        TEXT_COLOR,
                    ),
                    (
                        "Melhor partida",
                        best.map(|r| r.you.to_string())
                            .unwrap_or_else(|| "0".to_string()),
                        best.map(|r| r.time.with_timezone(&Local).format("%d/%m").to_string())
                            .unwrap_or_default(),
                        GEM,
                    ),
                ];

                for (i, (title, val, sub, col)) in cols.iter().enumerate() {
                    let cx = strip_rect.left() + (i as f32) * col_w;
                    if i > 0 {
                        sp.line_segment(
                            [
                                Pos2::new(cx, strip_rect.top()),
                                Pos2::new(cx, strip_rect.bottom()),
                            ],
                            Stroke::new(1.0_f32, STROKE),
                        );
                    }
                    let pad_x = if i == 0 { 30.0 } else { 22.0 };
                    sp.text(
                        Pos2::new(cx + pad_x, strip_rect.top() + 10.0),
                        egui::Align2::LEFT_TOP,
                        *title,
                        body_font(12.5),
                        MUTED,
                    );
                    sp.text(
                        Pos2::new(cx + pad_x, strip_rect.top() + 26.0),
                        egui::Align2::LEFT_TOP,
                        val,
                        display_font(36.0),
                        *col,
                    );
                    if !sub.is_empty() {
                        sp.text(
                            Pos2::new(cx + pad_x, strip_rect.bottom() - 18.0),
                            egui::Align2::LEFT_TOP,
                            sub,
                            body_font(11.5),
                            MUTED,
                        );
                    }
                }
            });

        ui.add_space(16.0);

        // ---- 2. CHART PANEL (takes flexible height, leaving 175px for Squad panels) ----
        let squad_height = 172.0;
        let chart_height = (ui.available_height() - squad_height - 16.0).max(170.0);

        Frame::NONE
            .fill(CARD)
            .stroke(Stroke::new(1.0_f32, STROKE))
            .corner_radius(CornerRadius::same(14))
            .inner_margin(Margin {
                left: 24,
                right: 24,
                top: 18,
                bottom: 20,
            })
            .show(ui, |ui| {
                ui.set_width(ui.available_width());
                ui.set_height(chart_height - 38.0);

                let bars = if n > 30 { &recs[n - 30..] } else { recs };
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("Baús por partida")
                            .font(body_font(13.5))
                            .color(TEXT_COLOR),
                    );
                    if !bars.is_empty() {
                        let b_avg =
                            bars.iter().map(|r| r.you as f64).sum::<f64>() / bars.len() as f64;
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            ui.label(
                                RichText::new(format!(
                                    "{} partidas · média {}",
                                    bars.len(),
                                    format_decimal_pt(b_avg, 1)
                                ))
                                .font(body_font(12.5))
                                .color(MUTED),
                            );
                        });
                    }
                });

                ui.add_space(12.0);
                let plot_avail = ui.available_size();
                let (plot_rect, _) = ui.allocate_exact_size(plot_avail, Sense::hover());
                let pp = ui.painter_at(plot_rect);

                // Baseline
                pp.line_segment(
                    [
                        Pos2::new(plot_rect.left(), plot_rect.bottom() - 1.0),
                        Pos2::new(plot_rect.right(), plot_rect.bottom() - 1.0),
                    ],
                    Stroke::new(1.0_f32, STROKE),
                );

                if bars.is_empty() {
                    pp.text(
                        Pos2::new(plot_rect.left(), plot_rect.top() + 8.0),
                        egui::Align2::LEFT_TOP,
                        "Clique em Ler replays para importar suas partidas salvas.",
                        body_font(13.0),
                        MUTED,
                    );
                } else {
                    let top_val = bars.iter().map(|r| r.you).max().unwrap_or(1).max(1);
                    let max_scale = (top_val as f32) * 1.18;
                    let slot_w = plot_rect.width() / (bars.len() as f32);
                    let pointer_pos = ui.input(|i| i.pointer.hover_pos());

                    for (idx, r) in bars.iter().enumerate() {
                        let slot_left = plot_rect.left() + (idx as f32) * slot_w;
                        let bar_w = (slot_w - 6.0).clamp(4.0, 30.0);
                        let bar_left = slot_left + (slot_w - bar_w) * 0.5;
                        let frac = ((r.you as f32) / max_scale).clamp(0.015, 1.0);
                        let bar_h = (plot_rect.height() * frac).max(3.0);
                        let bar_rect = Rect::from_min_max(
                            Pos2::new(bar_left, plot_rect.bottom() - bar_h),
                            Pos2::new(bar_left + bar_w, plot_rect.bottom() - 1.0),
                        );

                        let slot_rect = Rect::from_min_max(
                            Pos2::new(slot_left, plot_rect.top()),
                            Pos2::new(slot_left + slot_w, plot_rect.bottom()),
                        );
                        let hovered = pointer_pos.is_some_and(|p| slot_rect.contains(p));

                        let base_col = if r.you == top_val && top_val > 0 {
                            GEM
                        } else {
                            TEXT_COLOR
                        };
                        let fill_col = if hovered {
                            base_col.gamma_multiply(0.65)
                        } else {
                            base_col.gamma_multiply(0.92)
                        };

                        pp.rect_filled(
                            bar_rect,
                            CornerRadius {
                                nw: 3,
                                ne: 3,
                                sw: 0,
                                se: 0,
                            },
                            fill_col,
                        );

                        // Number label above bar
                        pp.text(
                            Pos2::new(
                                bar_rect.center().x,
                                (bar_rect.top() - 4.0).max(plot_rect.top() + 10.0),
                            ),
                            egui::Align2::CENTER_BOTTOM,
                            r.you.to_string(),
                            body_font(11.0),
                            MUTED,
                        );

                        if hovered {
                            let local_t = r.time.with_timezone(&Local).format("%d/%m, %H:%M");
                            egui::show_tooltip_at_pointer(
                                ui.ctx(),
                                ui.layer_id(),
                                Id::new(("bar_tip", idx)),
                                |ui| {
                                    ui.label(format!("{} baús, {}", r.you, local_t));
                                },
                            );
                        }
                    }
                }
            });

        ui.add_space(16.0);

        // ---- 3. SQUAD PANELS (2 columns side by side) ----
        let half_w = (ui.available_width() - 16.0) * 0.5;
        ui.horizontal_top(|ui| {
            ui.spacing_mut().item_spacing.x = 16.0;

            // Left Squad card: Último esquadrão
            Frame::NONE
                .fill(CARD)
                .stroke(Stroke::new(1.0_f32, STROKE))
                .corner_radius(CornerRadius::same(14))
                .inner_margin(Margin {
                    left: 24,
                    right: 24,
                    top: 16,
                    bottom: 16,
                })
                .show(ui, |ui| {
                    ui.set_width(half_w - 48.0);
                    ui.set_min_height(136.0);

                    let last_squad = recs.iter().rev().find(|r| !r.mates.is_empty());
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new("Último esquadrão")
                                .font(body_font(13.5))
                                .color(TEXT_COLOR),
                        );
                        if let Some(sq) = last_squad {
                            let sq_total: i32 =
                                sq.you + sq.mates.iter().map(|m| m.chests).sum::<i32>();
                            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                ui.label(
                                    RichText::new(format!("≥ {} no total", sq_total))
                                        .font(body_font(12.5))
                                        .color(MUTED),
                                );
                            });
                        }
                    });

                    ui.add_space(8.0);
                    if let Some(sq) = last_squad {
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("Você").font(body_font(13.0)).color(STEEL));
                            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                ui.label(
                                    RichText::new(sq.you.to_string())
                                        .font(display_font(19.0))
                                        .color(TEXT_COLOR),
                                );
                            });
                        });
                        for m in &sq.mates {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new(&m.name).font(body_font(13.0)).color(STEEL),
                                );
                                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                    ui.label(
                                        RichText::new(format!("≥ {}", m.chests))
                                            .font(display_font(19.0))
                                            .color(TEXT_COLOR),
                                    );
                                });
                            });
                        }
                    } else {
                        ui.label(
                            RichText::new("Leia os replays para ver seus colegas de equipe.")
                                .font(body_font(12.5))
                                .color(MUTED),
                        );
                    }
                });

            // Right Squad card: Colegas frequentes
            Frame::NONE
                .fill(CARD)
                .stroke(Stroke::new(1.0_f32, STROKE))
                .corner_radius(CornerRadius::same(14))
                .inner_margin(Margin {
                    left: 24,
                    right: 24,
                    top: 16,
                    bottom: 16,
                })
                .show(ui, |ui| {
                    ui.set_width(half_w - 48.0);
                    ui.set_min_height(136.0);

                    ui.label(
                        RichText::new("Colegas frequentes")
                            .font(body_font(13.5))
                            .color(TEXT_COLOR),
                    );
                    ui.add_space(8.0);

                    if top_mates.is_empty() {
                        ui.label(
                            RichText::new(
                                "Seus colegas aparecem aqui após a primeira partida em equipe.",
                            )
                            .font(body_font(12.5))
                            .color(MUTED),
                        );
                    } else {
                        for t in top_mates {
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new(&t.name).font(body_font(13.0)).color(STEEL),
                                );
                                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                    let p_word = if t.matches == 1 {
                                        "partida"
                                    } else {
                                        "partidas"
                                    };
                                    ui.label(
                                        RichText::new(format!(
                                            "{} {}  ·  ≥ {} baús",
                                            t.matches,
                                            p_word,
                                            format_thousands(t.chests)
                                        ))
                                        .font(body_font(12.5))
                                        .color(MUTED),
                                    );
                                });
                            });
                            ui.add_space(3.0);
                        }
                    }
                });
        });
    }

    fn render_right_column(
        &mut self,
        ui: &mut egui::Ui,
        recs: &[MatchRecord],
        season_chests: i64,
    ) {
        let now = Utc::now();
        let hour = current_or_next_hour(now);
        let live_hour = hour.as_ref().is_some_and(|h| is_hour_live(h, now));

        // ---- 1. TREASURE HOUR & ODDS CARD ----
        Frame::NONE
            .fill(CARD)
            .stroke(Stroke::new(1.0_f32, STROKE))
            .corner_radius(CornerRadius::same(14))
            .show(ui, |ui| {
                ui.set_width(ui.available_width());

                // Top Banner
                let banner_bg = if live_hour { GEM } else { CARD_ALT };
                let banner_fg = if live_hour { INK } else { TEXT_COLOR };
                Frame::NONE
                    .fill(banner_bg)
                    .corner_radius(CornerRadius {
                        nw: 13,
                        ne: 13,
                        sw: 0,
                        se: 0,
                    })
                    .inner_margin(Margin {
                        left: 24,
                        right: 24,
                        top: 18,
                        bottom: 18,
                    })
                    .show(ui, |ui| {
                        ui.set_width(ui.available_width());
                        if let Some(ref h) = hour {
                            let target = if live_hour { h.end_utc } else { h.start_utc };
                            let left = (target - now).num_seconds().max(0);
                            let days = left / 86400;
                            let hours = (left % 86400) / 3600;
                            let mins = (left % 3600) / 60;
                            let secs = left % 60;
                            let total_hours = left / 3600;

                            let countdown_str = if total_hours >= 24 {
                                format!("{}d {:02}:{:02}:{:02}", days, hours, mins, secs)
                            } else {
                                format!("{:02}:{:02}:{:02}", total_hours, mins, secs)
                            };

                            let title_str = if live_hour {
                                format!(
                                    "Hora do Tesouro até {}",
                                    h.end_utc.with_timezone(&Local).format("%H:%M")
                                )
                            } else {
                                let local_start = h.start_utc.with_timezone(&Local);
                                let wd = match local_start.format("%u").to_string().as_str() {
                                    "6" => "sáb.",
                                    "7" => "dom.",
                                    _ => "",
                                };
                                format!(
                                    "Próxima Hora do Tesouro: {} {}",
                                    wd,
                                    local_start.format("%H:%M")
                                )
                            };

                            let boost_str = h
                                .boosts
                                .iter()
                                .filter_map(|(k, b)| {
                                    ODDS.iter().find(|o| o.kind == *k).map(|o| {
                                        let b_fmt = if (*b - b.round()).abs() < 0.05 {
                                            format!("{:.0}", b)
                                        } else {
                                            format_decimal_pt(*b, 1)
                                        };
                                        format!("{} ×{}", o.label, b_fmt)
                                    })
                                })
                                .collect::<Vec<_>>()
                                .join("   ");

                            ui.label(
                                RichText::new(title_str)
                                    .font(body_font(13.5))
                                    .color(banner_fg),
                            );
                            ui.label(
                                RichText::new(countdown_str)
                                    .font(display_font(48.0))
                                    .color(banner_fg)
                                    .strong(),
                            );
                            ui.label(
                                RichText::new(boost_str)
                                    .font(body_font(12.0))
                                    .color(banner_fg.gamma_multiply(0.75)),
                            );
                        } else {
                            ui.label(
                                RichText::new("A caça ao tesouro terminou")
                                    .font(body_font(13.5))
                                    .color(banner_fg),
                            );
                        }
                    });

                // Odds Table + Prizes Found
                Frame::NONE
                    .inner_margin(Margin {
                        left: 24,
                        right: 24,
                        top: 18,
                        bottom: 20,
                    })
                    .show(ui, |ui| {
                        let week = week_at(now);
                        ui.horizontal(|ui| {
                            let wk_title = match week {
                                Some(w) => format!("Chances por baú · semana {}", w.number),
                                None => "Chances por baú".to_string(),
                            };
                            ui.label(
                                RichText::new(wk_title)
                                    .font(body_font(13.5))
                                    .color(TEXT_COLOR),
                            );
                            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                ui.label(
                                    RichText::new("chance acumulada")
                                        .font(body_font(11.5))
                                        .color(MUTED),
                                )
                                .on_hover_text(
                                    "Probabilidade de obter ao menos um item com os baús que você abriu nesta temporada.",
                                );
                            });
                        });

                        ui.add_space(10.0);

                        for o in ODDS {
                            let boost = if live_hour {
                                hour.as_ref()
                                    .and_then(|h| {
                                        h.boosts
                                            .iter()
                                            .find(|(k, _)| *k == o.kind)
                                            .map(|(_, b)| *b)
                                    })
                                    .unwrap_or(1.0)
                            } else {
                                1.0
                            };

                            let item_desc = if let Some(w) = week
                                && let Some(&name) = w.prizes.get(o.kind)
                            {
                                name
                            } else if o.kind == treasures::TOKEN {
                                "Desbloqueia a Loja Pirata"
                            } else if o.kind == treasures::GIZMO {
                                "Localizadores e outras engenhocas"
                            } else {
                                "Item da semana"
                            };

                            let p = chance_of_at_least_one(o.one_in, season_chests);
                            let luck = if p >= 0.995 {
                                ">99%".to_string()
                            } else if p < 0.001 {
                                "<0,1%".to_string()
                            } else if p < 0.1 {
                                format!("{}%", format_decimal_pt(p * 100.0, 1))
                            } else {
                                format!("{:.0}%", p * 100.0)
                            };

                            let effective_one_in = ((o.one_in as f64) / boost).round() as i64;
                            let odds_str = format!("1 em {}", format_thousands(effective_one_in));
                            let odds_col = if boost > 1.0 { GEM } else { TEXT_COLOR };

                            ui.horizontal(|ui| {
                                ui.vertical(|ui| {
                                    ui.set_max_width(185.0);
                                    ui.label(
                                        RichText::new(o.label)
                                            .font(body_font(13.0))
                                            .color(TEXT_COLOR),
                                    );
                                    ui.label(
                                        RichText::new(item_desc)
                                            .font(body_font(11.5))
                                            .color(MUTED),
                                    );
                                });

                                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                    ui.add_sized(
                                        Vec2::new(48.0, 20.0),
                                        egui::Label::new(
                                            RichText::new(luck)
                                                .font(body_font(12.0))
                                                .color(STEEL),
                                        ),
                                    );
                                    ui.add_space(6.0);
                                    ui.label(
                                        RichText::new(odds_str)
                                            .font(display_font(18.5))
                                            .color(odds_col),
                                    );
                                });
                            });
                            ui.add_space(5.0);
                        }

                        ui.add_space(6.0);
                        let (div, _) = ui.allocate_exact_size(
                            Vec2::new(ui.available_width(), 1.0),
                            Sense::hover(),
                        );
                        ui.painter().rect_filled(div, 0.0, STROKE);
                        ui.add_space(10.0);

                        // Finds & Pirate Shop Token
                        let mut finds: Vec<(&MatchRecord, &PrizeFind)> = recs
                            .iter()
                            .flat_map(|r| r.prizes.iter().map(move |p| (r, p)))
                            .collect();
                        finds.reverse();

                        let token_find = finds.iter().copied().find(|(_, p)| is_token(&p.prize_id));
                        if let Some((rec, prize)) = token_find {
                            ui.label(
                                RichText::new(format!(
                                    "Ficha da Loja Pirata encontrada por {} · {}",
                                    finder_label(prize),
                                    rec.time
                                        .with_timezone(&Local)
                                        .format("%d/%m, %H:%M")
                                ))
                                .font(body_font(12.5))
                                .color(GEM),
                            );
                        } else {
                            ui.label(
                                RichText::new("Ficha da Loja Pirata ainda não encontrada")
                                    .font(body_font(12.5))
                                    .color(TEXT_COLOR),
                            );
                        }

                        ui.add_space(4.0);
                        let other_prizes: Vec<(&MatchRecord, &PrizeFind)> = finds
                            .into_iter()
                            .filter(|(_, p)| !is_token(&p.prize_id))
                            .take(3)
                            .collect();

                        if other_prizes.is_empty() && token_find.is_none() {
                            ui.label(
                                RichText::new(
                                    "Os tesouros encontrados pelo esquadrão aparecem aqui.",
                                )
                                .font(body_font(12.5))
                                .color(MUTED),
                            );
                        } else {
                            for (rec, prize) in other_prizes {
                                let pname = prize_name(&prize.prize_id);
                                let f_lbl = finder_label(prize);
                                let dt = rec.time.with_timezone(&Local).format("%d/%m");
                                ui.horizontal_wrapped(|ui| {
                                    ui.spacing_mut().item_spacing.x = 0.0;
                                    ui.label(
                                        RichText::new(pname)
                                            .font(body_font(12.5))
                                            .color(TEXT_COLOR),
                                    );
                                    ui.label(
                                        RichText::new(format!(" — {}, {}", f_lbl, dt))
                                            .font(body_font(12.5))
                                            .color(MUTED),
                                    );
                                });
                            }
                        }
                    });
            });

        ui.add_space(16.0);

        // ---- 2. OVERLAY & SETTINGS CARD ----
        Frame::NONE
            .fill(CARD)
            .stroke(Stroke::new(1.0_f32, STROKE))
            .corner_radius(CornerRadius::same(14))
            .inner_margin(Margin {
                left: 24,
                right: 24,
                top: 18,
                bottom: 18,
            })
            .show(ui, |ui| {
                ui.set_width(ui.available_width());

                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("Sobreposição")
                            .font(body_font(13.5))
                            .color(TEXT_COLOR),
                    );
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.label(
                            RichText::new("http://localhost:7777/")
                                .font(body_font(12.0))
                                .color(STEEL),
                        )
                        .on_hover_text(
                            "Adicione este endereço como fonte do tipo Navegador no OBS ou Streamlabs.",
                        );
                    });
                });

                ui.add_space(12.0);

                // Title Input
                ui.horizontal(|ui| {
                    ui.add_sized(
                        Vec2::new(52.0, 28.0),
                        egui::Label::new(
                            RichText::new("Título").font(body_font(12.5)).color(MUTED),
                        ),
                    );
                    let resp = ui.add_sized(
                        Vec2::new(ui.available_width(), 30.0),
                        egui::TextEdit::singleline(&mut self.overlay_settings.title)
                            .font(body_font(13.0)),
                    );
                    if resp.changed() {
                        save_overlay_settings(&self.overlay_settings);
                    }
                });

                ui.add_space(8.0);

                // Color Input + Swatches
                ui.horizontal(|ui| {
                    ui.add_sized(
                        Vec2::new(52.0, 28.0),
                        egui::Label::new(RichText::new("Cor").font(body_font(12.5)).color(MUTED)),
                    );
                    let resp = ui.add_sized(
                        Vec2::new(82.0, 30.0),
                        egui::TextEdit::singleline(&mut self.overlay_settings.accent)
                            .font(body_font(13.0)),
                    );
                    if resp.changed() {
                        save_overlay_settings(&self.overlay_settings);
                    }

                    ui.add_space(8.0);
                    for (hex, col) in PRESET_COLORS {
                        let (swatch_rect, s_resp) =
                            ui.allocate_exact_size(Vec2::new(20.0, 20.0), Sense::click());
                        if s_resp.hovered() {
                            ui.ctx().set_cursor_icon(CursorIcon::PointingHand);
                        }
                        ui.painter().circle(
                            swatch_rect.center(),
                            9.5,
                            *col,
                            Stroke::new(1.0_f32, Color32::from_white_alpha(60)),
                        );
                        if s_resp.clicked() {
                            self.overlay_settings.accent = (*hex).to_string();
                            save_overlay_settings(&self.overlay_settings);
                        }
                    }
                });

                ui.add_space(14.0);
                let (div, _) =
                    ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), Sense::hover());
                ui.painter().rect_filled(div, 0.0, STROKE);
                ui.add_space(12.0);

                // In-game overlay checkbox
                let mut ig = self.app_settings.in_game_overlay;
                if ui
                    .checkbox(
                        &mut ig,
                        RichText::new("Exibir contador sobre o jogo")
                            .font(body_font(13.0))
                            .color(TEXT_COLOR),
                    )
                    .changed()
                {
                    self.app_settings.in_game_overlay = ig;
                    save_app_settings(&self.app_settings);
                }

                ui.add_space(2.0);
                ui.label(
                    RichText::new(
                        "Ctrl+Shift+O mostra ou oculta. Ctrl+Shift+L trava a posição para os cliques chegarem ao jogo.",
                    )
                    .font(body_font(11.5))
                    .color(MUTED),
                );

                ui.add_space(10.0);
                ui.label(
                    RichText::new(&self.overlay_status_text)
                        .font(body_font(11.5))
                        .color(MUTED),
                );
            });
    }

    fn render_in_game_overlay(
        &mut self,
        ctx: &egui::Context,
        live_count: Option<i32>,
        avg: f64,
        last: Option<i32>,
    ) {
        let locked = self.overlay_locked.load(Ordering::Relaxed);
        let default_pos = Pos2::new(
            self.app_settings.overlay_left.unwrap_or(840.0) as f32,
            self.app_settings.overlay_top.unwrap_or(24.0) as f32,
        );

        let viewport_id = ViewportId::from_hash_of("in_game_overlay");
        let builder = ViewportBuilder::default()
            .with_title("Bússola do Loot no jogo")
            .with_inner_size(Vec2::new(244.0, if locked { 166.0 } else { 188.0 }))
            .with_position(default_pos)
            .with_decorations(false)
            .with_transparent(true)
            .with_always_on_top()
            .with_taskbar(false)
            .with_resizable(false)
            .with_mouse_passthrough(locked);

        let title = if self.overlay_settings.title.trim().is_empty() {
            "Bússola do Loot".to_string()
        } else {
            self.overlay_settings.title.clone()
        };
        let accent_color = parse_hex_color(&self.overlay_settings.accent).unwrap_or(TEXT_COLOR);
        let live_str = live_count
            .map(|c| c.to_string())
            .unwrap_or_else(|| "—".to_string());
        let avg_str = format_decimal_pt(avg, 1);
        let last_str = last
            .map(|c| c.to_string())
            .unwrap_or_else(|| "—".to_string());

        ctx.show_viewport_immediate(viewport_id, builder, |ctx, _class| {
            egui::CentralPanel::default()
                .frame(Frame::NONE.fill(Color32::TRANSPARENT))
                .show(ctx, |ui| {
                    let rect = ui.max_rect().shrink(4.0);
                    let resp = ui.interact(rect, Id::new("overlay_drag"), Sense::click_and_drag());
                    if !locked && resp.drag_started() {
                        ctx.send_viewport_cmd(ViewportCommand::StartDrag);
                    }

                    Frame::NONE
                        .fill(Color32::from_rgba_unmultiplied(8, 8, 9, 242))
                        .stroke(Stroke::new(1.0_f32, Color32::from_white_alpha(50)))
                        .corner_radius(CornerRadius::same(10))
                        .inner_margin(Margin {
                            left: 18,
                            right: 20,
                            top: 15,
                            bottom: 13,
                        })
                        .show(ui, |ui| {
                            ui.set_width(ui.available_width());
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new(&title)
                                        .font(body_font(13.0))
                                        .color(TEXT_COLOR)
                                        .strong(),
                                );
                                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                                    let (dot_r, _) =
                                        ui.allocate_exact_size(Vec2::new(8.0, 8.0), Sense::hover());
                                    ui.painter().circle_filled(dot_r.center(), 4.0, GEM);
                                });
                            });

                            ui.add_space(6.0);
                            ui.label(
                                RichText::new("Baús nesta partida")
                                    .font(body_font(11.5))
                                    .color(MUTED),
                            );
                            ui.label(
                                RichText::new(&live_str)
                                    .font(display_font(56.0))
                                    .color(accent_color)
                                    .strong(),
                            );

                            let (div, _) = ui.allocate_exact_size(
                                Vec2::new(ui.available_width(), 1.0),
                                Sense::hover(),
                            );
                            ui.painter().rect_filled(div, 0.0, STROKE);
                            ui.add_space(6.0);

                            ui.horizontal(|ui| {
                                ui.vertical(|ui| {
                                    ui.label(
                                        RichText::new("Média").font(body_font(11.0)).color(MUTED),
                                    );
                                    ui.label(
                                        RichText::new(&avg_str)
                                            .font(display_font(21.0))
                                            .color(TEXT_COLOR)
                                            .strong(),
                                    );
                                });
                                ui.add_space(24.0);
                                ui.vertical(|ui| {
                                    ui.label(
                                        RichText::new("Última partida")
                                            .font(body_font(11.0))
                                            .color(MUTED),
                                    );
                                    ui.label(
                                        RichText::new(&last_str)
                                            .font(display_font(21.0))
                                            .color(TEXT_COLOR)
                                            .strong(),
                                    );
                                });
                            });

                            if !locked {
                                ui.add_space(6.0);
                                ui.label(
                                    RichText::new("Ctrl+Shift+O ocultar · Ctrl+Shift+L travar")
                                        .font(body_font(9.5))
                                        .color(Color32::from_rgb(0x6A, 0x6A, 0x72)),
                                );
                            }
                        });
                });
        });
    }
}

fn load_app_icon() -> Option<egui::IconData> {
    let icon_bytes = include_bytes!("../src/ChestCounter.Gui/Assets/token.png");
    let img = image::load_from_memory(icon_bytes).ok()?.resize(
        64,
        64,
        image::imageops::FilterType::Lanczos3,
    );
    let rgba = img.to_rgba8();
    let (width, height) = rgba.dimensions();
    Some(egui::IconData {
        rgba: rgba.into_raw(),
        width,
        height,
    })
}

fn main() -> eframe::Result {
    let mut viewport = ViewportBuilder::default()
        .with_title("Bússola do Loot")
        .with_inner_size(Vec2::new(1280.0, 910.0))
        .with_min_inner_size(Vec2::new(1100.0, 820.0));

    if let Some(icon) = load_app_icon() {
        viewport = viewport.with_icon(Arc::new(icon));
    }

    let options = eframe::NativeOptions {
        viewport,
        ..Default::default()
    };

    eframe::run_native(
        "Bússola do Loot",
        options,
        Box::new(|cc| Ok(Box::new(ChestCounterApp::new(cc)))),
    )
}
