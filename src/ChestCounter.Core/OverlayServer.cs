using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ChestCounter;

// Lightweight local HTTP/1.1 server (zero ASP.NET Core / Kestrel dependency) that serves a
// transparent stream overlay (OBS / Streamlabs Browser Source) plus /stats.json and /config.json.
public sealed class OverlayServer : IDisposable
{
    private static readonly byte[] HtmlBytes = Encoding.UTF8.GetBytes(Html);
    private static readonly byte[] OkJsonBytes = "{\"ok\":true}"u8.ToArray();
    private static readonly byte[] EmptyJsonBytes = "{}"u8.ToArray();
    private static readonly Lazy<byte[]> ChestPngBytes = new(LoadChestPng);

    private readonly List<TcpListener> _listeners = new();
    private CancellationTokenSource? _cts;

    public volatile string StatsJson = "{}";
    public Func<int?, string>? StatsBuilder; // builds stats JSON given the live count (preferred)
    public Func<string>? ConfigProvider;     // returns full style/branding JSON, read fresh per request
    public volatile int LiveCountRaw = -1;   // -1 = null (no live match count)
    public int Port { get; }

    public int? LiveCount
    {
        get => LiveCountRaw < 0 ? null : LiveCountRaw;
        set => LiveCountRaw = value ?? -1;
    }

    public OverlayServer(int port = AppChannel.OverlayPort)
    {
        Port = port;
    }

    private static byte[] LoadChestPng()
    {
        using var icon = typeof(OverlayServer).Assembly.GetManifestResourceStream("ChestCounter.Chest.png")!;
        using var ms = new MemoryStream((int)icon.Length);
        icon.CopyTo(ms);
        return ms.ToArray();
    }

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        var ipv4 = new TcpListener(IPAddress.Loopback, Port);
        TcpListener? ipv6 = null;

        try
        {
            ipv4.Start();
            _listeners.Add(ipv4);

            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                    ipv6.Start();
                    _listeners.Add(ipv6);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    throw;
                }
                catch
                {
                    try { ipv6?.Stop(); } catch { }
                }
            }

            _cts = cts;
            foreach (var listener in _listeners)
                _ = AcceptLoopAsync(listener, cts.Token);
        }
        catch
        {
            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { }
            }
            _listeners.Clear();
            cts.Dispose();
            throw;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 2048, leaveOpen: true);

                while (!ct.IsCancellationRequested)
                {
                    string? requestLine;
                    try
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                        requestLine = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
                    }
                    catch { break; }

                    if (string.IsNullOrEmpty(requestLine)) break;

                    string? host = null;
                    bool hasOrigin = false;
                    bool closeRequested = false;
                    int contentLength = 0;

                    while (true)
                    {
                        string? header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(header)) break;

                        int colon = header.IndexOf(':');
                        if (colon <= 0) continue;
                        var name = header.AsSpan(0, colon).Trim();
                        var value = header.AsSpan(colon + 1).Trim();

                        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                            host = value.ToString();
                        else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                            hasOrigin = true;
                        else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                            closeRequested = value.Contains("close", StringComparison.OrdinalIgnoreCase);
                        else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int len) && len > 0)
                            contentLength = len;
                    }

                    if (contentLength > 0)
                    {
                        char[] discard = new char[Math.Min(contentLength, 4096)];
                        int remaining = contentLength;
                        while (remaining > 0)
                        {
                            int n = await reader.ReadAsync(discard.AsMemory(0, Math.Min(remaining, discard.Length)), ct).ConfigureAwait(false);
                            if (n <= 0) break;
                            remaining -= n;
                        }
                    }

                    int sp1 = requestLine.IndexOf(' ');
                    int sp2 = sp1 > 0 ? requestLine.IndexOf(' ', sp1 + 1) : -1;
                    if (sp1 <= 0 || sp2 <= sp1 + 1)
                    {
                        await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", [], keepAlive: false, ct).ConfigureAwait(false);
                        break;
                    }

                    string method = requestLine[..sp1];
                    string target = requestLine[(sp1 + 1)..sp2];
                    int qIdx = target.IndexOf('?');
                    string path = qIdx >= 0 ? target[..qIdx] : target;
                    string query = qIdx >= 0 ? target[(qIdx + 1)..] : "";

                    bool keepAlive = !closeRequested && requestLine.EndsWith("HTTP/1.1", StringComparison.OrdinalIgnoreCase);

                    if (!IsLocalHostHeader(host))
                    {
                        await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", [], keepAlive: false, ct).ConfigureAwait(false);
                        break;
                    }

                    if (path == "/live" && method.Equals("POST", StringComparison.OrdinalIgnoreCase) && !hasOrigin)
                    {
                        string? nParam = TryGetQueryParam(query, "n");
                        if (string.IsNullOrEmpty(nParam)) LiveCount = null;
                        else if (int.TryParse(nParam, out var count) && count >= 0) LiveCount = count;
                        else
                        {
                            await WriteResponseAsync(stream, 400, "application/json", [], keepAlive, ct).ConfigureAwait(false);
                            if (!keepAlive) break;
                            continue;
                        }
                        await WriteResponseAsync(stream, 200, "application/json", OkJsonBytes, keepAlive, ct).ConfigureAwait(false);
                    }
                    else if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, 405, "text/plain; charset=utf-8", [], keepAlive, ct).ConfigureAwait(false);
                    }
                    else if (path == "/chest.png")
                    {
                        await WriteResponseAsync(stream, 200, "image/png", ChestPngBytes.Value, keepAlive, ct).ConfigureAwait(false);
                    }
                    else if (path == "/stats.json")
                    {
                        string json = StatsBuilder?.Invoke(LiveCount) ?? StatsJson;
                        await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), keepAlive, ct).ConfigureAwait(false);
                    }
                    else if (path == "/config.json")
                    {
                        string json = ConfigProvider?.Invoke() ?? "{}";
                        byte[] bytes = json == "{}" ? EmptyJsonBytes : Encoding.UTF8.GetBytes(json);
                        await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", bytes, keepAlive, ct).ConfigureAwait(false);
                    }
                    else if (path == "/")
                    {
                        await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", HtmlBytes, keepAlive, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", [], keepAlive, ct).ConfigureAwait(false);
                    }

                    if (!keepAlive) break;
                }
            }
            catch { }
        }
    }

    private static bool IsLocalHostHeader(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string name = host.Trim();
        if (name.StartsWith('['))
        {
            int end = name.IndexOf(']');
            if (end < 0) return false;
            name = name[..(end + 1)];
            return name == "[::1]";
        }
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1";
    }

    private static string? TryGetQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            var k = eq >= 0 ? part[..eq] : part;
            if (k.Equals(key, StringComparison.Ordinal))
                return eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..]) : "";
        }
        return null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string contentType, byte[] body, bool keepAlive, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
        string headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
        _listeners.Clear();
        cts.Dispose();
    }

    private const string Html = """
<!DOCTYPE html>
<html lang="pt-BR"><head><meta charset="utf-8">
<title>Bússola do Loot · OBS Overlay</title>
<style>
  :root {
    --accent: #F5B942;
    --secondary: #4CC9F0;
    --text: #F8FAFC;
    --label: #A8B5C8;
    --gem: #FF4D73;
    --emerald: #3DDC84;
    --num-font: 'Bahnschrift Condensed','Bahnschrift','Segoe UI',sans-serif;
    --stroke-px: 3.2px;
    --scale: 1;
    --hud-op: 0.98;
  }
  html, body {
    margin: 0; padding: 14px; background: transparent; overflow: hidden;
    font-family: 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;
    user-select: none;
  }
  #wrap {
    display: inline-block;
    position: relative;
    transform: scale(var(--scale));
    transform-origin: top left;
    opacity: var(--hud-op);
    transition: transform .16s ease, opacity .2s ease;
  }
  #wrap.pulse { transform: scale(calc(var(--scale) * 1.08)); }
  #plate {
    position: absolute; inset: 0;
    pointer-events: none;
    box-sizing: border-box;
    transition: background .2s, border .2s, border-radius .2s, transform .2s;
  }
  #slash {
    position: absolute; left: 0; top: 5px; bottom: 5px; width: 3.5px;
    border-radius: 2px; background: var(--accent);
  }
  #content {
    position: relative; z-index: 2;
    color: var(--text);
  }
  .outlined {
    paint-order: stroke fill;
    -webkit-text-stroke: var(--stroke-px) rgba(4, 6, 11, 0.94);
  }
  .shadowed { filter: drop-shadow(0 1.5px 3.5px rgba(0,0,0,0.78)); }
  .icon-wrap { position: relative; display: inline-flex; align-items: center; justify-content: center; flex-shrink: 0; }
  .icon-wrap img { display: block; object-fit: contain; }
  .dot {
    width: 7px; height: 7px; border-radius: 50%;
    background: var(--gem); border: 1.4px solid rgba(5,7,13,0.92);
    box-sizing: border-box; flex-shrink: 0;
  }
  .dot.active { background: var(--emerald); }
  .icon-wrap .dot { position: absolute; right: -1px; bottom: -1px; }
  .hdr {
    display: flex; align-items: center; justify-content: space-between;
    gap: 10px; margin-bottom: 4px; font-size: 10.5px; font-weight: 700;
  }
  .hdr-status { font-size: 8.5px; letter-spacing: .04em; text-transform: uppercase; }
  .num { font-family: var(--num-font); font-weight: 700; font-variant-numeric: tabular-nums; line-height: 0.94; }
  .tag { font-size: 8.2px; font-weight: 700; color: var(--label); letter-spacing: .04em; text-transform: uppercase; }
  /* Tactical */
  .ly-tactical { display: flex; align-items: center; gap: 0; }
  .ly-tactical .sep { width: 1.2px; height: 26px; background: rgba(255,255,255,0.22); margin: 0 10px; }
  .ly-tactical .mgrid { display: grid; gap: 2px 11px; }
  .ly-tactical .mcell { display: flex; align-items: baseline; justify-content: space-between; gap: 6px; }
  /* Pill */
  .ly-pill { display: flex; align-items: center; gap: 0; white-space: nowrap; }
  .ly-pill .pdot { width: 3.5px; height: 3.5px; border-radius: 50%; background: rgba(255,255,255,0.28); margin: 0 8px; }
  /* Micro */
  .ly-micro { display: flex; align-items: center; white-space: nowrap; }
  /* Vertical */
  .ly-vertical { min-width: 102px; display: flex; flex-direction: column; }
  .ly-vertical .hsep { height: 1px; background: rgba(255,255,255,0.18); margin: 4px 0; }
  .ly-vertical .vrow { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; margin: 1px 0; }
  /* Card */
  .ly-card { width: 222px; }
  .ly-card .hero { display: flex; align-items: flex-end; justify-content: space-between; }
  .ly-card .hsep { height: 1px; background: rgba(255,255,255,0.18); margin: 6px 0 5px; }
  .ly-card .strip { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
  .ly-card .th-banner {
    margin-top: 5px; padding: 3px 7px; border-radius: 5px;
    background: rgba(255,61,107,0.22); border: 1px solid rgba(255,61,107,0.38);
    display: flex; align-items: center; justify-content: space-between; font-size: 9.5px; font-weight: 700;
  }
  #plusBadge {
    position: absolute; right: -6px; top: -8px; z-index: 5;
    padding: 1px 6px; border-radius: 7px;
    background: rgba(9,12,20,0.78); border: 1px solid var(--accent);
    color: var(--accent); font-family: var(--num-font); font-weight: 700; font-size: 13px;
    opacity: 0; transition: opacity .18s; pointer-events: none;
  }
  #plusBadge.show { opacity: 1; }
</style></head>
<body>
  <div id="wrap">
    <div id="plate"><div id="slash"></div></div>
    <div id="content" class="shadowed"></div>
    <div id="plusBadge" class="outlined">+1</div>
  </div>
<script>
  let cfgState = {
    title: 'Bússola do Loot', accent: '#F5B942', secondaryAccent: '#4CC9F0',
    backgroundHex: '#0A0F1A', textHex: '#F8FAFC', layout: 'tactical', chrome: 'angled',
    fontStyle: 'fortnite', bgOpacity: 0.34, hudOpacity: 0.98, scale: 1.0,
    textStroke: 1.65, borderStrength: 0.36, textShadow: true, accentBar: true,
    showIcon: true, showLive: true, showSeason: true, showToken: true,
    showAvg: false, showLast: false, showBest: false, showTreasureHour: false,
    showHeader: false, showLabels: true, pulseOnChest: true, syncObsStyle: true
  };
  let statsState = { live: null, total: 0, avg: 0, last: null, best: 0, tokenChance: '0,000%', treasureHour: '', treasureHourLive: false };
  let prevLive = null;
  let pulseTimer = null;

  function hexToRgb(hex, fb) {
    const m = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec((hex||'').trim());
    return m ? [parseInt(m[1],16), parseInt(m[2],16), parseInt(m[3],16)] : fb;
  }

  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  function render() {
    const c = cfgState.syncObsStyle === false ? {
      ...cfgState, layout: 'card', chrome: 'glass', bgOpacity: 0.85, textStroke: 1.2,
      showHeader: true, showIcon: true, showLive: true, showSeason: true, showToken: true, showAvg: true, showLast: true
    } : cfgState;

    const root = document.documentElement.style;
    root.setProperty('--accent', c.accent || '#F5B942');
    root.setProperty('--secondary', c.secondaryAccent || '#4CC9F0');
    root.setProperty('--text', c.textHex || '#F8FAFC');
    root.setProperty('--stroke-px', ((c.textStroke ?? 1.6) * 1.85).toFixed(2) + 'px');
    root.setProperty('--scale', c.scale || 1);
    root.setProperty('--hud-op', c.hudOpacity ?? 0.98);
    root.setProperty('--num-font',
      c.fontStyle === 'modern' ? "'Segoe UI Variable Display','Segoe UI',sans-serif" :
      c.fontStyle === 'mono' ? "'Cascadia Mono','Consolas',monospace" :
      "'Bahnschrift Condensed','Bahnschrift','Segoe UI',sans-serif");

    const plate = document.getElementById('plate');
    const slash = document.getElementById('slash');
    const content = document.getElementById('content');
    content.classList.toggle('shadowed', !!c.textShadow);

    const noChrome = c.chrome === 'none' || ((c.bgOpacity || 0) <= 0.01 && (c.borderStrength || 0) <= 0.01);
    if (noChrome) {
      plate.style.display = 'none';
      content.style.padding = '2px 4px';
    } else {
      plate.style.display = 'block';
      const [br, bg, bb] = hexToRgb(c.backgroundHex, [10, 15, 26]);
      const [ar, ag, ab] = hexToRgb(c.accent, [245, 185, 66]);
      const a1 = Math.min(1, Math.max(0, c.bgOpacity ?? 0.34));
      const a2 = Math.min(1, a1 * 1.12);
      const tr = Math.round(br * 0.9 + ar * 0.1), tg = Math.round(bg * 0.9 + ag * 0.1), tb = Math.round(bb * 0.9 + ab * 0.1);
      plate.style.background = `linear-gradient(145deg, rgba(${tr},${tg},${tb},${a1.toFixed(3)}), rgba(${br},${bg},${bb},${a2.toFixed(3)}))`;

      const bs = c.borderStrength ?? 0.36;
      plate.style.border = bs > 0.02 ? `1px solid rgba(${Math.round(255*0.6+ar*0.4)},${Math.round(255*0.6+ag*0.4)},${Math.round(255*0.6+ab*0.4)},${(bs*0.8).toFixed(3)})` : 'none';

      const angled = c.chrome === 'angled' && c.layout !== 'card' && c.layout !== 'vertical';
      plate.style.transform = angled ? 'skewX(-8.5deg)' : 'none';
      plate.style.borderRadius = c.chrome === 'capsule' ? (c.layout === 'card' || c.layout === 'vertical' ? '16px' : '24px') :
                                 c.chrome === 'sharp' ? '3px' : c.chrome === 'angled' ? '6px' : '10px';
      slash.style.display = (c.accentBar && c.chrome !== 'capsule') ? 'block' : 'none';

      const padL = (c.accentBar && c.chrome !== 'capsule' ? 4 : 0) + (angled ? 5 : 0);
      const padR = angled ? 5 : 0;
      const baseP = c.layout === 'micro' ? [3, 8] : c.layout === 'pill' ? [5, 12] : c.layout === 'card' ? [9, 13] : [6, 12];
      content.style.padding = `${baseP[0]}px ${baseP[1] + padR}px ${baseP[0]}px ${baseP[1] + padL}px`;
    }

    const d = statsState;
    const isLive = d.live !== null && d.live !== undefined;
    const liveStr = isLive ? String(d.live) : '—';
    const metrics = [];
    if (c.showSeason) metrics.push({ k: 'TEMP', v: (d.total||0).toLocaleString('pt-BR'), col: 'var(--text)' });
    if (c.showToken) metrics.push({ k: 'TOKEN', v: d.tokenChance || '0,000%', col: 'var(--secondary)' });
    if (c.showAvg) metrics.push({ k: 'MÉD', v: (d.avg||0).toLocaleString('pt-BR',{minimumFractionDigits:1,maximumFractionDigits:1}), col: 'var(--text)' });
    if (c.showLast) metrics.push({ k: 'ÚLT', v: d.last != null ? String(d.last) : '—', col: 'var(--text)' });
    if (c.showBest) metrics.push({ k: 'MÁX', v: String(d.best||0), col: 'var(--accent)' });
    if (c.showTreasureHour && d.treasureHour) metrics.push({ k: 'HORA', v: d.treasureHour, col: d.treasureHourLive ? 'var(--gem)' : 'var(--secondary)' });

    const iconHtml = sz => c.showIcon
      ? `<span class="icon-wrap" style="width:${sz}px;height:${sz}px"><img src="/chest.png" width="${sz}" height="${sz}" alt=""/><span class="dot ${isLive?'active':''}"></span></span>`
      : (!c.showHeader ? `<span class="dot ${isLive?'active':''}"></span>` : '');

    const hdrHtml = c.showHeader
      ? `<div class="hdr"><span style="display:flex;align-items:center;gap:6px"><span class="dot ${isLive?'active':''}"></span><span class="outlined">${esc(c.title)}</span></span><span class="hdr-status outlined" style="color:${isLive?'var(--emerald)':'var(--label)'}">${isLive?'AO VIVO':'ESPERA'}</span></div>`
      : '';

    let body = '';
    if (c.layout === 'pill') {
      const parts = [];
      if (c.showIcon || !c.showHeader) parts.push(iconHtml(20));
      if (c.showLive) parts.push(`<span class="num outlined" style="font-size:22px;color:var(--accent);margin-left:${c.showIcon||!c.showHeader?6:0}px">${esc(liveStr)}</span>`);
      metrics.forEach((m, idx) => {
        if (parts.length > 0 && (c.showLive || idx > 0 || c.showIcon)) parts.push(`<span class="pdot"></span>`);
        parts.push(`<span style="display:inline-flex;align-items:baseline;gap:4px">${c.showLabels?`<span class="tag outlined">${m.k}</span>`:''}<span class="num outlined" style="font-size:15.5px;color:${m.col}">${esc(m.v)}</span></span>`);
      });
      body = `${hdrHtml}<div class="ly-pill">${parts.join('')}</div>`;
    } else if (c.layout === 'micro') {
      let s = `${iconHtml(17)}`;
      if (c.showLive) s += `<span class="num outlined" style="font-size:20px;color:var(--accent);margin-left:5px">${esc(liveStr)}</span>`;
      metrics.slice(0, 2).forEach(m => {
        s += `<span class="outlined" style="margin:0 5px;color:var(--label);font-size:11px">·</span>`;
        if (c.showLabels) s += `<span class="tag outlined" style="margin-right:3px">${m.k}</span>`;
        s += `<span class="num outlined" style="font-size:13.5px;color:${m.col}">${esc(m.v)}</span>`;
      });
      body = `<div class="ly-micro">${s}</div>`;
    } else if (c.layout === 'vertical') {
      let top = '';
      if (c.showIcon || c.showLive) {
        top = `<div style="display:flex;align-items:center;gap:6px">${iconHtml(22)}${c.showLive?`<div>${c.showLabels?'<div class="tag outlined" style="margin-bottom:-2px">PARTIDA</div>':''}<div class="num outlined" style="font-size:26px;color:var(--accent)">${esc(liveStr)}</div></div>`:''}</div>`;
      }
      const rows = metrics.map(m => `<div class="vrow">${c.showLabels?`<span class="tag outlined">${m.k}</span>`:''}<span class="num outlined" style="font-size:14px;color:${m.col};margin-left:auto">${esc(m.v)}</span></div>`).join('');
      body = `${hdrHtml}<div class="ly-vertical">${top}${top && rows ? '<div class="hsep"></div>' : ''}${rows}</div>`;
    } else if (c.layout === 'card') {
      const rem = metrics.filter(m => m.k !== 'TEMP' && m.k !== 'HORA');
      const thHtml = (c.showTreasureHour && d.treasureHour)
        ? `<div class="th-banner"><span class="outlined">${d.treasureHourLive?'Hora do Tesouro AO VIVO':'Próxima Hora do Tesouro'}</span><span class="num outlined" style="font-size:12.5px;color:${d.treasureHourLive?'var(--gem)':'var(--secondary)'}">${esc(d.treasureHour)}</span></div>`
        : '';
      const stripHtml = rem.length > 0
        ? `<div class="hsep"></div><div class="strip">${rem.map(m => `<span style="display:inline-flex;align-items:baseline;gap:4px">${c.showLabels?`<span class="tag outlined">${m.k}</span>`:''}<span class="num outlined" style="font-size:13.5px;color:${m.col}">${esc(m.v)}</span></span>`).join('')}</div>`
        : '';
      body = `<div class="ly-card">${hdrHtml}<div class="hero"><div style="display:flex;align-items:center;gap:7px">${c.showIcon?iconHtml(28):''}${c.showLive?`<div>${c.showLabels?'<div class="tag outlined">NESTA PARTIDA</div>':''}<div class="num outlined" style="font-size:34px;color:var(--accent)">${esc(liveStr)}</div></div>`:''}</div>${c.showSeason?`<div style="text-align:right">${c.showLabels?'<div class="tag outlined">TEMPORADA</div>':''}<div class="num outlined" style="font-size:20px">${(d.total||0).toLocaleString('pt-BR')}</div></div>`:''}</div>${thHtml}${stripHtml}</div>`;
    } else {
      // Tactical (default)
      const hasLeft = c.showIcon || c.showLive;
      const left = hasLeft ? `<div style="display:flex;align-items:center;gap:7px">${iconHtml(25)}${c.showLive?`<div>${c.showLabels?'<div class="tag outlined" style="margin-bottom:-2px">PARTIDA</div>':''}<div class="num outlined" style="font-size:28px;color:var(--accent)">${esc(liveStr)}</div></div>`:''}</div>` : '';
      const cols = metrics.length <= 2 ? 1 : Math.ceil(metrics.length / 2);
      const mgrid = metrics.length > 0 ? `<div class="mgrid" style="grid-template-columns:repeat(${cols},auto)">${metrics.map(m => `<div class="mcell">${c.showLabels?`<span class="tag outlined">${m.k}</span>`:''}<span class="num outlined" style="font-size:${metrics.length===1?17:14}px;color:${m.col}">${esc(m.v)}</span></div>`).join('')}</div>` : '';
      body = `${hdrHtml}<div class="ly-tactical">${left}${hasLeft && metrics.length > 0 ? `<div class="sep" style="transform:${c.chrome==='angled'?'skewX(-8.5deg)':'none'}"></div>` : ''}${mgrid}</div>`;
    }
    content.innerHTML = body;
  }

  async function tick() {
    try {
      const d = await (await fetch('/stats.json?' + Date.now())).json();
      const isLive = d.live !== null && d.live !== undefined;
      if (isLive && prevLive !== null && d.live > prevLive && cfgState.pulseOnChest !== false) {
        const wrap = document.getElementById('wrap');
        const pb = document.getElementById('plusBadge');
        pb.textContent = '+' + (d.live - prevLive);
        pb.classList.add('show');
        wrap.classList.add('pulse');
        setTimeout(() => wrap.classList.remove('pulse'), 200);
        clearTimeout(pulseTimer);
        pulseTimer = setTimeout(() => pb.classList.remove('show'), 1500);
      }
      prevLive = isLive ? d.live : null;
      statsState = d;
      render();
    } catch (e) {}
  }

  async function cfg() {
    try {
      cfgState = await (await fetch('/config.json?' + Date.now())).json();
      render();
    } catch (e) {}
  }

  cfg().then(tick);
  setInterval(tick, 1000);
  setInterval(cfg, 1800);
</script>
</body></html>
""";
}
