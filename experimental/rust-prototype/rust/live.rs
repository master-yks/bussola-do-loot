//! Headless live chest counter + overlay server (`chestcounter-live.exe`), plus CLI diagnostic modes:
//!   chestcounter-live                 run the live counter and host/feed the overlay
//!   chestcounter-live --scan          scan all replays in Demos folder and save stats
//!   chestcounter-live shot.png ...    OCR test mode on image file(s)

use chest_counter::ocr::{LiveChestCounter, test_image_ocr};
use chest_counter::overlay_server::OverlayServer;
use chest_counter::replay::{find_replays, scan_replays};
use chest_counter::stats::{load_stats, save_stats};
use std::io::Write;
use std::path::Path;
use std::sync::{Arc, Mutex, atomic::Ordering};
use std::time::{Duration, Instant};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();

    if args.iter().any(|a| a == "--scan") {
        let t0 = Instant::now();
        let files = find_replays(None);
        println!("Encontrados {} replays.", files.len());
        let mut stats = load_stats();
        let run = scan_replays(
            &files,
            &mut stats,
            Some(|done, total, name: &str, err: Option<&str>| {
                if let Some(e) = err {
                    println!("[{}/{}] {} -> ERRO: {}", done, total, name, e);
                }
            }),
        );
        if run.parsed > 0 {
            save_stats(&stats);
        }
        println!(
            "Concluído em {:.2}s: Lidos={}, Novas={}, Já contadas={}, Fora do modo={}, Falhas={}, Total Baús={}",
            t0.elapsed().as_secs_f64(),
            run.parsed,
            run.new_matches,
            run.already_counted,
            run.skipped_mode,
            run.failed,
            stats.your_chests()
        );
        return;
    }

    if !args.is_empty() {
        for arg in &args {
            if let Err(e) = test_image_ocr(Path::new(arg)) {
                eprintln!("Erro ao testar {}: {}", arg, e);
            }
        }
        return;
    }

    let counter = LiveChestCounter::new();
    if !counter.available {
        eprintln!("Instale o OCR de português (Brasil) nas opções de idioma do Windows.");
        std::process::exit(1);
    }

    let stats = Arc::new(Mutex::new(load_stats()));
    let server = OverlayServer::start(7777, stats.clone(), counter.live_raw.clone());
    if let Some(ref srv) = server {
        println!(
            "Overlay:  http://localhost:{}/   (fonte Navegador do OBS)",
            srv.port
        );
    } else {
        println!(
            "A porta do OBS está indisponível. Tentando enviar a contagem ao painel local."
        );
    }

    let live_raw = counter.live_raw.clone();
    let log_text = counter.last_log_text.clone();
    let has_server = server.is_some();

    counter.start(
        stats,
        Arc::new(move || {
            let n = live_raw.load(Ordering::Relaxed);
            if let Ok(lg) = log_text.lock() {
                if !lg.is_empty() {
                    println!("{}", *lg);
                }
            }
            if !has_server {
                let param = if n < 0 {
                    String::new()
                } else {
                    n.to_string()
                };
                if let Ok(mut stream) = std::net::TcpStream::connect_timeout(
                    &"127.0.0.1:7777".parse().unwrap(),
                    Duration::from_millis(300),
                ) {
                    let req = format!(
                        "POST /live?n={} HTTP/1.1\r\nHost: localhost:7777\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        param
                    );
                    let _ = stream.write_all(req.as_bytes());
                }
            }
        }),
    );

    println!("Contador ao vivo. Jogue Fortnite em janela sem bordas. Ctrl+C para sair.\n");
    loop {
        std::thread::sleep(Duration::from_secs(3600));
    }
}
