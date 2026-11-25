//! Tiny local HTTP server for the OBS / Streamlabs Browser Source overlay on localhost:7777.
//! Zero heavy framework dependencies — uses pure `std::net::TcpListener` on loopback.

use crate::stats::{LifetimeStats, overlay_config_json, overlay_json};
use std::io::{Read, Write};
use std::net::TcpListener;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicI32, Ordering},
};
use std::thread;

pub struct OverlayServer {
    pub port: u16,
    pub live_count: Arc<AtomicI32>, // -1 = None
    pub stats: Arc<Mutex<LifetimeStats>>,
    stop_flag: Arc<AtomicBool>,
}

impl OverlayServer {
    pub fn start(port: u16, stats: Arc<Mutex<LifetimeStats>>, live_count: Arc<AtomicI32>) -> Option<Self> {
        let addr = format!("127.0.0.1:{}", port);
        let listener = TcpListener::bind(&addr).ok()?;
        let stop_flag = Arc::new(AtomicBool::new(false));

        let stop_clone = stop_flag.clone();
        let stats_clone = stats.clone();
        let live_clone = live_count.clone();

        thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                if stop_clone.load(Ordering::Relaxed) {
                    break;
                }
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_millis(500)));
                let _ = stream.set_write_timeout(Some(std::time::Duration::from_millis(500)));
                handle_client(stream, &stats_clone, &live_clone);
            }
        });

        Some(Self {
            port,
            live_count,
            stats,
            stop_flag,
        })
    }

    pub fn stop(&self) {
        self.stop_flag.store(true, Ordering::Relaxed);
        let _ = std::net::TcpStream::connect(format!("127.0.0.1:{}", self.port));
    }
}

impl Drop for OverlayServer {
    fn drop(&mut self) {
        self.stop();
    }
}

fn handle_client(
    mut stream: std::net::TcpStream,
    stats: &Arc<Mutex<LifetimeStats>>,
    live_count: &Arc<AtomicI32>,
) {
    let mut buf = [0u8; 2048];
    let mut n = 0;
    let deadline = std::time::Instant::now() + std::time::Duration::from_millis(500);
    while !buf[..n].windows(4).any(|w| w == b"\r\n\r\n") {
        if n == buf.len() || std::time::Instant::now() >= deadline {
            send_response(&mut stream, 400, "text/plain", "Bad Request");
            return;
        }
        match stream.read(&mut buf[n..]) {
            Ok(0) | Err(_) => return,
            Ok(read) => n += read,
        }
    }
    let req = String::from_utf8_lossy(&buf[..n]);
    let mut lines = req.lines();
    let Some(first_line) = lines.next() else {
        return;
    };
    let mut parts = first_line.split_whitespace();
    let method = parts.next().unwrap_or("");
    let full_path = parts.next().unwrap_or("");
    let (path, query) = match full_path.find('?') {
        Some(idx) => (&full_path[..idx], &full_path[idx + 1..]),
        None => (full_path, ""),
    };

    let mut local_host = false;
    let mut has_origin = false;
    for line in lines.take_while(|line| !line.is_empty()) {
        let Some((key, value)) = line.split_once(':') else {
            send_response(&mut stream, 400, "text/plain", "Bad Request");
            return;
        };
        if key.eq_ignore_ascii_case("host") {
            let value = value.trim();
            let host = value.split_once(':').map_or(value, |(host, _)| host);
            if local_host || !(host.eq_ignore_ascii_case("localhost") || host == "127.0.0.1") {
                send_response(&mut stream, 400, "text/plain", "Bad Request");
                return;
            }
            local_host = true;
        }
        has_origin |= key.eq_ignore_ascii_case("origin");
    }
    if !local_host {
        send_response(&mut stream, 400, "text/plain", "Bad Request");
        return;
    }

    if path == "/live" && method == "POST" && !has_origin {
        let mut n_param: Option<&str> = None;
        for kv in query.split('&') {
            if let Some(val) = kv.strip_prefix("n=") {
                n_param = Some(val);
            }
        }
        match n_param {
            None | Some("") => {
                live_count.store(-1, Ordering::Relaxed);
                send_response(&mut stream, 200, "application/json", "{\"ok\":true}");
            }
            Some(v) => {
                if let Ok(c) = v.parse::<i32>()
                    && c >= 0
                {
                    live_count.store(c, Ordering::Relaxed);
                    send_response(&mut stream, 200, "application/json", "{\"ok\":true}");
                } else {
                    send_response(&mut stream, 400, "text/plain", "Bad Request");
                }
            }
        }
        return;
    }

    if method != "GET" {
        send_response(&mut stream, 405, "text/plain", "Method Not Allowed");
        return;
    }

    match path {
        "/stats.json" => {
            let live_raw = live_count.load(Ordering::Relaxed);
            let live = if live_raw < 0 { None } else { Some(live_raw) };
            let body = if let Ok(st) = stats.lock() {
                overlay_json(&st, live)
            } else {
                "{}".to_string()
            };
            send_response(&mut stream, 200, "application/json", &body);
        }
        "/config.json" => {
            let body = overlay_config_json();
            send_response(&mut stream, 200, "application/json", &body);
        }
        "/" => {
            send_response(&mut stream, 200, "text/html; charset=utf-8", OVERLAY_HTML);
        }
        _ => {
            send_response(&mut stream, 404, "text/plain", "Not Found");
        }
    }
}

fn send_response(stream: &mut std::net::TcpStream, status: u16, content_type: &str, body: &str) {
    let status_text = match status {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Error",
    };
    let bytes = body.as_bytes();
    let header = format!(
        "HTTP/1.1 {} {}\r\nContent-Type: {}\r\nContent-Length: {}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
        status,
        status_text,
        content_type,
        bytes.len()
    );
    let _ = stream.write_all(header.as_bytes());
    let _ = stream.write_all(bytes);
}

const OVERLAY_HTML: &str = r#"<!DOCTYPE html>
<html lang="pt-BR"><head><meta charset="utf-8">
<title>Bússola do Loot para OBS</title>
<style>
  :root{ --accent:#F3F1EC; --text:#F3F1EC; --muted:#8A8A90; --gem:#FF3D6B; }
  html,body{margin:0;background:transparent;overflow:hidden;font-family:'Segoe UI',system-ui,sans-serif;}
  .card{
    width:290px; padding:18px 22px 14px; box-sizing:border-box;
    background:rgba(5,5,5,.93); border:1px solid rgba(255,255,255,.16); border-radius:10px;
    box-shadow:0 18px 48px rgba(0,0,0,.6); color:var(--text);
  }
  .hdr{display:flex;align-items:center;gap:9px;font-weight:600;font-size:13px;}
  .hdr .dot{width:8px;height:8px;border-radius:50%;background:var(--gem);margin-left:auto;
    animation:pulse 2.2s ease-out infinite;}
  @keyframes pulse{0%{box-shadow:0 0 0 0 rgba(255,61,107,.55)}70%{box-shadow:0 0 0 10px rgba(255,61,107,0)}100%{box-shadow:0 0 0 0 rgba(255,61,107,0)}}
  .label{margin-top:14px;font-size:12px;color:var(--muted);}
  .live{font-stretch:75%;font-weight:800;font-size:76px;line-height:.9;letter-spacing:-.01em;
        color:var(--accent);font-variant-numeric:tabular-nums;transform-origin:left center;transition:transform .14s ease;}
  .live.pulse-n{transform:scale(1.08)}
  .sub{display:flex;gap:28px;margin-top:12px;padding-top:12px;border-top:1px solid rgba(255,255,255,.14)}
  .stat .k{font-size:11.5px;color:var(--muted);}
  .stat .v{font-stretch:75%;font-weight:700;font-size:24px;margin-top:1px;font-variant-numeric:tabular-nums;}
  .wm{margin-top:10px;font-size:10.5px;color:rgba(255,255,255,.42);text-align:right;}
</style></head>
<body>
  <div class="card">
    <div class="hdr"><span id="title">Bússola do Loot</span><span class="dot"></span></div>
    <div class="label">Baús abertos nesta partida</div>
    <div class="live" id="live">0</div>
    <div class="sub">
      <div class="stat"><div class="k">Média</div><div class="v" id="avg">0,0</div></div>
      <div class="stat"><div class="k">Última partida</div><div class="v" id="last">&#8212;</div></div>
    </div>
    <div class="wm" id="wm"></div>
  </div>
<script>
  let prev = null;
  async function tick(){
    try{
      const d = await (await fetch('/stats.json?'+Date.now())).json();
      const live = (d.live==null) ? 0 : d.live;
      const el = document.getElementById('live');
      if(prev!==null && live>prev){ el.classList.add('pulse-n'); setTimeout(()=>el.classList.remove('pulse-n'),170); }
      prev = live; el.textContent = live;
      document.getElementById('avg').textContent  = (d.avg||0).toLocaleString('pt-BR', {minimumFractionDigits:1,maximumFractionDigits:1});
      document.getElementById('last').textContent = (d.last!=null) ? d.last : '—';
    }catch(e){}
  }
  async function cfg(){
    try{
      const c = await (await fetch('/config.json?'+Date.now())).json();
      if(c.accent) document.documentElement.style.setProperty('--accent', c.accent);
      if(c.title)  document.getElementById('title').textContent = c.title;
      document.getElementById('wm').textContent = c.domain || '';
    }catch(e){}
  }
  tick(); setInterval(tick, 1000);
  cfg();  setInterval(cfg, 2500);
</script>
</body></html>
"#;
