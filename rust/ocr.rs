//! Real-time Live Chest Counter using Windows.Media.Ocr + GDI BitBlt screen capture + Demos folder watcher.
//! Includes enhanced multi-pass thresholding and tolerant OCR regex so no chest opening is missed.

use crate::replay::{find_replays, scan_replays};
use crate::stats::{LifetimeStats, save_stats};
use anyhow::Result;
use chrono::Local;
use regex::Regex;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicI32, Ordering},
    LazyLock,
};
use std::thread;
use std::time::Duration;

// Enhanced tolerant regex for chest XP popups:
// Matches:
// - BAÚ, BAU, BAÚS, BAUS, BAÜ, BÁU, B4U
// - BAÚ RARO, BAU RARO, BAÚ VASCULHADO, BAÚ ABERTO, BAÚ DE FACÇÃO, BAÚ SOTERRADO, BAÚ ENTERRADO, BAÚ DO TESOURO, BAÚ MÍTICO
// - ABRIU UM BAÚ, VASCULHOU UM BAÚ, VASCULHOU BAÚ
// - CHEST, CHE5T, RARE CHEST, SEARCHED CHEST, BURIED CHEST, TREASURE CHEST, FACTION CHEST
// Plus optional multiplier: x2, ×2, X3, K2, *2
static CHEST_LABEL_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"(?i)^[\s\p{P}]*(?:B[AÁÀÂÃ4][UÚÙÛÜ0]S?(?:\s+(?:RAROS?|ABERTOS?|VASCULHADOS?|SOTERRADOS?|ENTERRADOS?|DOURADOS?|M[IÍ]TICOS?|DE\s+\w+|DO\s+\w+))?|(?:ABRIU|VASCULHOU)\s+(?:UM\s+)?B[AÁÀÂÃ4][UÚÙÛÜ0]|(?:RARE\s+|SEARCHED\s+|BURIED\s+|TREASURE\s+|FACTION\s+)?CHE[S5]TS?)[\s\p{P}]*(?:[XK×\*]\s*([1-9]\d?))?[\s\p{P}]*$"
    ).unwrap()
});

// Matches XP / EXP / PE values: "+600 EXP", "600 XP", "+1.200 PE", "+ 250 EXP", etc.
static EXPERIENCE_RE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)(?:^|[^\p{L}\p{N}_])[+]?\s*\d[\d.,\s]*\s*(?:EXP|XP|PE|EX\s*P)(?:$|[^\p{L}])")
        .unwrap()
});

pub fn has_experience(line: &str) -> bool {
    EXPERIENCE_RE.is_match(line)
}

pub fn count_chests_in_lines(lines: &[String]) -> i32 {
    let mut total = 0;
    for line in lines {
        let stripped = EXPERIENCE_RE.replace_all(line, "");
        if let Some(caps) = CHEST_LABEL_RE.captures(&stripped) {
            let mult = caps
                .get(1)
                .and_then(|m| m.as_str().parse::<i32>().ok())
                .unwrap_or(1);
            total += mult;
        }
    }
    total
}

#[cfg(windows)]
pub mod win_ocr {
    use super::*;
    use windows::Globalization::Language;
    use windows::Graphics::Imaging::{BitmapPixelFormat, SoftwareBitmap};
    use windows::Media::Ocr::OcrEngine;
    use windows::Storage::Streams::{DataWriter, InMemoryRandomAccessStream};
    use windows::Win32::Foundation::{HWND, POINT, RECT};
    use windows::Win32::Graphics::Gdi::{
        BI_RGB, BITMAPINFO, BITMAPINFOHEADER, BitBlt, ClientToScreen, CreateCompatibleBitmap,
        CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC, DeleteObject, GetDC, GetDIBits, ReleaseDC,
        SRCCOPY, SelectObject,
    };
    use windows::Win32::System::Threading::{
        OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
        QueryFullProcessImageNameW,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        GetClientRect, GetForegroundWindow, GetWindowTextW, GetWindowThreadProcessId,
    };
    use windows::core::HSTRING;

    pub fn create_ocr_engine() -> Option<(OcrEngine, String)> {
        if let Ok(lang) = Language::CreateLanguage(&HSTRING::from("pt-BR")) {
            if let Ok(engine) = OcrEngine::TryCreateFromLanguage(&lang) {
                let tag = engine
                    .RecognizerLanguage()
                    .ok()
                    .and_then(|l| l.LanguageTag().ok())
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| "pt-BR".to_string());
                return Some((engine, tag));
            }
        }
        if let Ok(engine) = OcrEngine::TryCreateFromUserProfileLanguages() {
            let tag = engine
                .RecognizerLanguage()
                .ok()
                .and_then(|l| l.LanguageTag().ok())
                .map(|s| s.to_string())
                .unwrap_or_else(|| "pt-BR".to_string());
            return Some((engine, tag));
        }
        None
    }

    pub fn is_fortnite_window(hwnd: HWND) -> bool {
        if hwnd.0.is_null() {
            return false;
        }
        unsafe {
            let mut buf = [0u16; 256];
            let len = GetWindowTextW(hwnd, &mut buf);
            if len > 0 {
                let title = String::from_utf16_lossy(&buf[..len as usize]);
                let trimmed = title.trim();
                if trimmed.eq_ignore_ascii_case("Fortnite")
                    || trimmed.starts_with("Fortnite ")
                    || trimmed.starts_with("FortniteClient")
                {
                    return true;
                }
            }
            let mut pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut pid));
            if pid == 0 {
                return false;
            }
            if let Ok(proc) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
                let mut pbuf = [0u16; 512];
                let mut plen = pbuf.len() as u32;
                let ok = QueryFullProcessImageNameW(
                    proc,
                    PROCESS_NAME_WIN32,
                    windows::core::PWSTR(pbuf.as_mut_ptr()),
                    &mut plen,
                );
                let _ = windows::Win32::Foundation::CloseHandle(proc);
                if ok.is_ok() && plen > 0 {
                    let exe_path = String::from_utf16_lossy(&pbuf[..plen as usize]);
                    let lower = exe_path.to_ascii_lowercase();
                    if lower.contains("fortniteclient-") || lower.ends_with("fortnite.exe") {
                        return true;
                    }
                }
            }
        }
        false
    }

    pub fn get_popup_rect(hwnd: HWND) -> Option<(i32, i32, i32, i32)> {
        unsafe {
            let mut rect = RECT::default();
            if GetClientRect(hwnd, &mut rect).is_err() {
                return None;
            }
            let w = rect.right - rect.left;
            let h = rect.bottom - rect.top;
            if w <= 100 || h <= 100 {
                return None;
            }
            let mut pt = POINT { x: 0, y: 0 };
            if !ClientToScreen(hwnd, &mut pt).as_bool() {
                return None;
            }
            let px = pt.x + ((w as f64) * 0.58) as i32;
            let py = pt.y + ((h as f64) * 0.24) as i32;
            let pw = ((w as f64) * 0.28) as i32;
            let ph = ((h as f64) * 0.24) as i32;
            Some((px, py, pw, ph))
        }
    }

    /// Captures screen rectangle (x, y, width, height) into a 32-bit BGRA buffer (top-down).
    pub fn capture_screen_bgra(x: i32, y: i32, width: i32, height: i32) -> Option<Vec<u8>> {
        if width <= 0 || height <= 0 {
            return None;
        }
        unsafe {
            let screen_dc = GetDC(HWND(std::ptr::null_mut()));
            if screen_dc.0.is_null() {
                return None;
            }
            let mem_dc = CreateCompatibleDC(screen_dc);
            let bmp = CreateCompatibleBitmap(screen_dc, width, height);
            let old = SelectObject(mem_dc, bmp);

            let ok = BitBlt(mem_dc, 0, 0, width, height, screen_dc, x, y, SRCCOPY).is_ok();

            let mut pixels = vec![0u8; (width as usize) * (height as usize) * 4];
            if ok {
                let mut bmi = BITMAPINFO {
                    bmiHeader: BITMAPINFOHEADER {
                        biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                        biWidth: width,
                        biHeight: -height, // negative = top-down
                        biPlanes: 1,
                        biBitCount: 32,
                        biCompression: BI_RGB.0,
                        ..Default::default()
                    },
                    ..Default::default()
                };
                GetDIBits(
                    mem_dc,
                    bmp,
                    0,
                    height as u32,
                    Some(pixels.as_mut_ptr() as *mut _),
                    &mut bmi,
                    DIB_RGB_COLORS,
                );
            }

            SelectObject(mem_dc, old);
            let _ = DeleteObject(bmp);
            let _ = DeleteDC(mem_dc);
            ReleaseDC(HWND(std::ptr::null_mut()), screen_dc);

            if ok {
                Some(pixels)
            } else {
                None
            }
        }
    }

    /// Bilinear upscale + brightness threshold into Gray8 SoftwareBitmap and run Windows.Media.Ocr.
    pub fn read_white_text(
        engine: &OcrEngine,
        bgra: &[u8],
        width: usize,
        height: usize,
        scale: usize,
        min_brightness: u8,
    ) -> Vec<String> {
        if width == 0 || height == 0 || bgra.len() < width * height * 4 {
            return Vec::new();
        }
        let max_dim = OcrEngine::MaxImageDimension().unwrap_or(4096) as usize;
        let factor = (scale as f64).min(max_dim as f64 / (width.max(height) as f64));
        let dst_w = ((width as f64 * factor) as usize).max(1);
        let dst_h = ((height as f64 * factor) as usize).max(1);

        let mut gray = vec![255u8; dst_w * dst_h];
        let inv_f = 1.0 / factor;

        for dy in 0..dst_h {
            let sy_f = (dy as f64 + 0.5) * inv_f - 0.5;
            let y0 = (sy_f.floor() as isize).clamp(0, height as isize - 1) as usize;
            let y1 = (y0 + 1).min(height - 1);
            let wy = (sy_f - y0 as f64).clamp(0.0, 1.0);

            for dx in 0..dst_w {
                let sx_f = (dx as f64 + 0.5) * inv_f - 0.5;
                let x0 = (sx_f.floor() as isize).clamp(0, width as isize - 1) as usize;
                let x1 = (x0 + 1).min(width - 1);
                let wx = (sx_f - x0 as f64).clamp(0.0, 1.0);

                let i00 = (y0 * width + x0) * 4;
                let i10 = (y0 * width + x1) * 4;
                let i01 = (y1 * width + x0) * 4;
                let i11 = (y1 * width + x1) * 4;

                let mut is_white = true;
                for c in 0..3 {
                    let v0 = bgra[i00 + c] as f64 * (1.0 - wx) + bgra[i10 + c] as f64 * wx;
                    let v1 = bgra[i01 + c] as f64 * (1.0 - wx) + bgra[i11 + c] as f64 * wx;
                    let val = (v0 * (1.0 - wy) + v1 * wy) as u8;
                    if val < min_brightness {
                        is_white = false;
                        break;
                    }
                }
                if is_white {
                    gray[dy * dst_w + dx] = 0; // black text on white background
                }
            }
        }

        let Ok(stream) = InMemoryRandomAccessStream::new() else {
            return Vec::new();
        };
        let Ok(writer) = DataWriter::CreateDataWriter(&stream) else {
            return Vec::new();
        };
        if writer.WriteBytes(&gray).is_err() {
            return Vec::new();
        }
        let Ok(ibuf) = writer.DetachBuffer() else {
            return Vec::new();
        };
        let Ok(sbmp) =
            SoftwareBitmap::CreateCopyFromBuffer(&ibuf, BitmapPixelFormat::Gray8, dst_w as i32, dst_h as i32)
        else {
            return Vec::new();
        };
        let Ok(async_op) = engine.RecognizeAsync(&sbmp) else {
            return Vec::new();
        };
        let Ok(result) = async_op.get() else {
            return Vec::new();
        };
        let Ok(lines) = result.Lines() else {
            return Vec::new();
        };

        let mut out = Vec::new();
        for line in lines {
            if let Ok(text) = line.Text() {
                let t = text.to_string();
                let trimmed = t.trim();
                if !trimmed.is_empty() {
                    out.push(trimmed.to_string());
                }
            }
        }
        out
    }

    pub fn chests_in_popup(
        engine: &OcrEngine,
        bgra: &[u8],
        width: usize,
        height: usize,
        last_popup: &mut String,
        mut on_popup_read: impl FnMut(&str),
    ) -> i32 {
        let a = read_white_text(engine, bgra, width, height, 2, 190);
        let b = read_white_text(engine, bgra, width, height, 3, 205);

        let first_has_xp = a.iter().any(|l| has_experience(l));
        let second_has_xp = b.iter().any(|l| has_experience(l));

        let mut first_count = count_chests_in_lines(&a);
        let second_count = count_chests_in_lines(&b);

        // Third fallback pass with lower threshold (170) for dimmer displays / fade-in frames
        if !first_has_xp && !second_has_xp {
            let c = read_white_text(engine, bgra, width, height, 2, 170);
            if c.iter().any(|l| has_experience(l)) {
                first_count = count_chests_in_lines(&c);
                let popup = c.join(" / ");
                if popup != *last_popup {
                    *last_popup = popup.clone();
                    on_popup_read(&popup);
                }
                return first_count;
            }
            return 0;
        }

        let lines = if first_has_xp && (!second_has_xp || first_count >= second_count) {
            &a
        } else {
            &b
        };
        let popup = lines.join(" / ");
        if popup != *last_popup {
            *last_popup = popup.clone();
            on_popup_read(&popup);
        }
        first_count.max(second_count)
    }

    pub fn foreground_window() -> HWND {
        unsafe { GetForegroundWindow() }
    }
}

pub struct LiveChestCounter {
    pub available: bool,
    pub language_tag: String,
    pub live_raw: Arc<AtomicI32>, // -1 = None (between matches), >= 0 = live count
    pub last_popup_text: Arc<Mutex<String>>,
    pub last_log_text: Arc<Mutex<String>>,
    stop_flag: Arc<AtomicBool>,
}

impl LiveChestCounter {
    pub fn new() -> Self {
        #[cfg(windows)]
        let (available, language_tag) = match win_ocr::create_ocr_engine() {
            Some((_, tag)) => (true, tag),
            None => (false, String::new()),
        };
        #[cfg(not(windows))]
        let (available, language_tag) = (false, String::new());

        Self {
            available,
            language_tag,
            live_raw: Arc::new(AtomicI32::new(-1)),
            last_popup_text: Arc::new(Mutex::new(String::new())),
            last_log_text: Arc::new(Mutex::new(String::new())),
            stop_flag: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn start(
        &self,
        stats: Arc<Mutex<LifetimeStats>>,
        on_update: Arc<dyn Fn() + Send + Sync>,
    ) {
        if !self.available {
            return;
        }
        self.stop_flag.store(false, Ordering::Relaxed);

        // Thread 1: MatchWatchLoop (monitors %LOCALAPPDATA%\FortniteGame\Saved\Demos)
        {
            let stop = self.stop_flag.clone();
            let live_raw = self.live_raw.clone();
            let stats_clone = stats.clone();
            let log_text = self.last_log_text.clone();
            let notify = on_update.clone();

            thread::spawn(move || {
                let mut observed: HashMap<PathBuf, u64> = HashMap::new();
                let mut pending: HashSet<PathBuf> = HashSet::new();
                let mut active: Option<PathBuf> = None;
                let mut first_pass = true;

                while !stop.load(Ordering::Relaxed) {
                    let files = find_replays(None);
                    for file in files.iter().rev() {
                        let length = std::fs::metadata(file).map(|m| m.len()).unwrap_or(0);
                        let prev = observed.get(file).copied();
                        if prev != Some(length) {
                            observed.insert(file.clone(), length);
                            if !first_pass && pending.insert(file.clone()) {
                                active = Some(file.clone());
                                // Only reset to 0 if we were between matches (-1), so if a chest was
                                // already opened just before the replay file flushed, we don't lose it!
                                let _ = live_raw.compare_exchange(
                                    -1,
                                    0,
                                    Ordering::Relaxed,
                                    Ordering::Relaxed,
                                );
                                notify();
                            }
                            continue;
                        }
                        if !pending.contains(file) {
                            continue;
                        }
                        if let Ok(mut st) = stats_clone.lock() {
                            let run = scan_replays(std::slice::from_ref(file), &mut st, None::<fn(usize, usize, &str, Option<&str>)>);
                            if run.parsed == 0 && run.already_counted == 0 {
                                continue;
                            }
                            if run.parsed > 0 {
                                save_stats(&st);
                            }
                            pending.remove(file);
                            if active.as_ref() == Some(file) && !stop.load(Ordering::Relaxed) {
                                active = None;
                                live_raw.store(-1, Ordering::Relaxed);
                            }
                            if let Ok(mut lg) = log_text.lock() {
                                *lg = format!(
                                    "[{}] Replay concluído. Partidas salvas: {}.",
                                    Local::now().format("%H:%M:%S"),
                                    st.matches()
                                );
                            }
                            notify();
                        }
                    }
                    first_pass = false;
                    let present: HashSet<PathBuf> = files.into_iter().collect();
                    observed.retain(|k, _| present.contains(k));
                    pending.retain(|k| present.contains(k));

                    for _ in 0..20 {
                        if stop.load(Ordering::Relaxed) {
                            return;
                        }
                        thread::sleep(Duration::from_millis(200));
                    }
                }
            });
        }

        // Thread 2: CaptureLoop (high-speed screen capture + Windows.Media.Ocr)
        #[cfg(windows)]
        {
            let stop = self.stop_flag.clone();
            let live_raw = self.live_raw.clone();
            let popup_text = self.last_popup_text.clone();
            let log_text = self.last_log_text.clone();
            let notify = on_update;

            thread::spawn(move || {
                let Some((engine, _)) = win_ocr::create_ocr_engine() else {
                    return;
                };
                let mut last_hwnd = isize::MAX;
                let mut game_in_focus = false;
                let mut credited = 0i32;
                let mut empty_frames = 0i32;
                let mut last_popup = String::new();

                while !stop.load(Ordering::Relaxed) {
                    let hwnd = win_ocr::foreground_window();
                    let hwnd_val = hwnd.0 as isize;
                    if hwnd_val != last_hwnd {
                        game_in_focus = win_ocr::is_fortnite_window(hwnd);
                        last_hwnd = hwnd_val;
                    }
                    if !game_in_focus {
                        thread::sleep(Duration::from_millis(500));
                        continue;
                    }
                    let Some((px, py, pw, ph)) = win_ocr::get_popup_rect(hwnd) else {
                        thread::sleep(Duration::from_millis(250));
                        continue;
                    };
                    let Some(bgra) = win_ocr::capture_screen_bgra(px, py, pw, ph) else {
                        thread::sleep(Duration::from_millis(120));
                        continue;
                    };

                    let n = win_ocr::chests_in_popup(
                        &engine,
                        &bgra,
                        pw as usize,
                        ph as usize,
                        &mut last_popup,
                        |p| {
                            if let Ok(mut pt) = popup_text.lock() {
                                *pt = p.to_string();
                            }
                            notify();
                        },
                    );

                    if stop.load(Ordering::Relaxed) {
                        return;
                    }

                    if n > credited {
                        let cur = live_raw.load(Ordering::Relaxed).max(0);
                        let next = cur + (n - credited);
                        live_raw.store(next, Ordering::Relaxed);
                        credited = n;
                        if let Ok(mut lg) = log_text.lock() {
                            *lg = format!(
                                "[{}] Baú aberto (total nesta partida: {})",
                                Local::now().format("%H:%M:%S"),
                                next
                            );
                        }
                        notify();
                    }

                    if n == 0 {
                        empty_frames += 1;
                    } else {
                        empty_frames = 0;
                    }
                    if empty_frames >= 4 {
                        credited = 0;
                    }

                    thread::sleep(Duration::from_millis(100));
                }
            });
        }
    }

    pub fn stop(&self) {
        self.stop_flag.store(true, Ordering::Relaxed);
        self.live_raw.store(-1, Ordering::Relaxed);
    }
}

/// Helper for testing OCR on an image file (`chestcounter-live image.png`).
pub fn test_image_ocr(path: &std::path::Path) -> Result<()> {
    #[cfg(windows)]
    {
        let Some((engine, tag)) = win_ocr::create_ocr_engine() else {
            anyhow::bail!("OCR de português (Brasil) indisponível no Windows.");
        };
        println!("Idioma do OCR: {}", tag);
        let img = image::open(path)?.to_rgba8();
        let (w, h) = img.dimensions();
        // Convert RGBA to BGRA
        let mut bgra = img.into_raw();
        for px in bgra.chunks_exact_mut(4) {
            px.swap(0, 2);
        }
        let mut last_popup = String::new();
        let chests = win_ocr::chests_in_popup(&engine, &bgra, w as usize, h as usize, &mut last_popup, |p| {
            println!("{}: \"{}\"", path.display(), p);
        });
        println!("{} · baús: {}", path.display(), chests);
    }
    Ok(())
}
