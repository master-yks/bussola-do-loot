using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using ChestCounter;
using ChestCounter.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

var culture = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Headless live chest counter + CLI scanner (`chestcounter-live.exe`)
//   chestcounter-live                 run the live counter and host/feed the overlay
//   chestcounter-live --scan          parallel scan all replays in Demos folder and print summary
//   chestcounter-live shot.png ...    OCR test mode on image file(s)

if (args.Any(a => a.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
{
    var sw = Stopwatch.StartNew();
    var files = ChestCounterService.FindReplays();
    Console.WriteLine($"Encontrados {files.Count} replays em {ChestCounterService.DefaultDemosPath}");
    var (run, stats) = ChestCounterService.Scan(files, (done, total, name, err) =>
    {
        if (err != null) Console.WriteLine($"[{done}/{total}] {name} -> ERRO: {err}");
    });
    sw.Stop();
    var season = stats.Over(ChestCounterService.SeasonStartUtc);
    double tokenProb = TreasureCatalog.ChanceOfAtLeastOne(137_500, season.YourChests);
    Console.WriteLine(
        $"Concluído em {sw.Elapsed.TotalSeconds:F2}s: Lidos={run.Parsed}, Novas={run.NewMatches}, JáContadas={run.AlreadyCounted}, " +
        $"ForaDoModo={run.SkippedMode}, EmGravação={run.ActiveRecording}, Abandonadas={run.AbandonedCrash}, Falhas={run.Failed}");
    Console.WriteLine(
        $"Temporada: {season.YourChests:N0} baús em {season.Matches} partidas (média {season.AvgYou:F1}) · " +
        $"Chance Acumulada Token Pirata: {TreasureCatalog.FormatChance(tokenProb, true)} · {ChestCounterService.Credits}");
    Console.WriteLine($"Colegas de equipe: ≥ {season.SquadChests - season.YourChests:N0} baús visíveis na temporada.");
    if (run.Failed > 0) Environment.ExitCode = 1;
    return;
}

var counter = new LiveChestCounter();
if (args.Length > 0)
{
    if (!counter.Available)
    {
        Console.Error.WriteLine("Instale o OCR de português (Brasil) nas opções de idioma do Windows.");
        Environment.ExitCode = 1;
        return;
    }
    Console.WriteLine($"Idioma do OCR: {counter.LanguageTag}");
    var engine = OcrEngine.TryCreateFromLanguage(new Language(counter.LanguageTag!));
    foreach (var path in args)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Arquivo não encontrado: {path}");
            Environment.ExitCode = 1;
            continue;
        }
        try
        {
            using var input = File.OpenRead(path);
            using var imageStream = input.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(imageStream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            var raw = await engine!.RecognizeAsync(bitmap);
            Console.WriteLine($"{Path.GetFileName(path)}: \"{raw.Text}\"");
            using var shot = new Bitmap(path);
            int chests = await counter.ChestsInPopup(shot);
            Console.WriteLine($"{Path.GetFileName(path)} · baús: {chests}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Falha ao ler {Path.GetFileName(path)}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
    return;
}

Console.WriteLine($"Bússola do Loot · {ChestCounterService.Credits}");
if (!counter.Available)
    Console.Error.WriteLine("Aviso: OCR indisponível no Windows; apenas o monitoramento automático de replays ficará ativo.");

OverlayServer? server = new OverlayServer()
{
    ConfigProvider = OverlayConfig.Json,
    StatsBuilder = live => ChestCounterService.OverlayJson(ChestCounterService.LoadStats(), live),
};
try { server.Start(); Console.WriteLine($"Overlay OBS: http://localhost:{server.Port}/"); }
catch { server = null; Console.WriteLine($"Porta {AppChannel.OverlayPort} já em uso pelo painel principal; enviando contagem ao painel."); }

using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(1) };
counter.Log += Console.WriteLine;
counter.LiveChanged += async n =>
{
    if (server != null) server.LiveCount = n;
    else
    {
        try { using var response = await http.PostAsync($"{AppChannel.OverlayUrl}live?n={n?.ToString() ?? ""}", null); }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
    }
};

counter.Start();
Console.WriteLine("Monitoramento ativo. Jogue Fortnite em janela sem bordas. Ctrl+C para sair.\n");
Thread.Sleep(Timeout.Infinite);
