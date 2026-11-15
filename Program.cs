using System.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// --- Start: én kjørende kopi, feillogg og «avslutt»-støtte (programmet har ikke eget vindu når det er installert) ---
var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sangvisning");
Directory.CreateDirectory(appDataDir);
AppDomain.CurrentDomain.UnhandledException += (_, e) => { try { File.AppendAllText(Path.Combine(appDataDir, "feil.txt"), DateTime.Now + "\n" + e.ExceptionObject + "\n\n"); } catch { } };
using var enInstans = new Mutex(true, "Local\\Sangvisning-en-instans", out var forsteInstans);
if (!forsteInstans)
{
    // Programmet kjører allerede: åpne bare vinduet mot det som kjører.
    try
    {
        var pt = File.ReadAllText(Path.Combine(appDataDir, "port.txt")).Trim();
        var ad = $"http://127.0.0.1:{pt}/index.html";
        var ok = false;
        foreach (var nl in new[] { "chrome", "msedge" })
        {
            try { Process.Start(new ProcessStartInfo(nl, $"--app={ad} --start-maximized") { UseShellExecute = true }); ok = true; break; } catch { }
        }
        if (!ok) Process.Start(new ProcessStartInfo(ad) { UseShellExecute = true });
    }
    catch { }
    return;
}

// Programmet åpnes på denne maskinen. Sangerskjermen (stage.html) kan i tillegg åpnes fra mobiler på samme nettverk.
// Alt annet (kontrollsiden, sanger, sikkerhetskopier) er låst til denne maskinen.
// Start med «dotnet run -- --kun-lokal» for å slå av tilgang fra andre enheter helt.
// Porten kan endres under Mer ▸ Innstillinger (lagres i innstillinger.json). Er den opptatt, brukes neste ledige.
int cfgPort = 5000, port = 5000;
try
{
    var cf0 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sangvisning", "innstillinger.json");
    if (File.Exists(cf0))
    {
        using var pd = JsonDocument.Parse(File.ReadAllText(cf0));
        if (pd.RootElement.TryGetProperty("port", out var pp) && pp.ValueKind == JsonValueKind.Number && pp.TryGetInt32(out var pv) && pv >= 1024 && pv <= 65535) cfgPort = pv;
    }
}
catch { }
bool PortFree(int p) { try { var l = new TcpListener(IPAddress.Any, p); l.Start(); l.Stop(); return true; } catch { return false; } }
port = cfgPort;
for (var tries = 0; tries < 30 && !PortFree(port); tries++) port = port >= 65535 ? 5000 : port + 1;
var portNote = port != cfgPort ? $"Port {cfgPort} er opptatt. Bruker {port} i stedet." : "";
var url = $"http://127.0.0.1:{port}";
bool lan = !args.Contains("--kun-lokal");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--kun-lokal").ToArray());
builder.WebHost.UseUrls(lan ? $"http://0.0.0.0:{port}" : url);
var app = builder.Build();

// Data lagres her: %AppData%\Sangvisning\data.json
//   data.json.bak            = forrige versjon (oppdateres ved hver lagring)
//   Sikkerhetskopi\data-ÅÅÅÅ-MM-DD.json = én kopi per dag, siste 30 beholdes
var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sangvisning");
Directory.CreateDirectory(dir);
var file = Path.Combine(dir, "data.json");
var bdir = Path.Combine(dir, "Sikkerhetskopi");
Directory.CreateDirectory(bdir);
var nameRx = new Regex(@"^data-\d{4}-\d{2}-\d{2}\.json$");

string backupExtra = "", backupExtraLast = "";
void DailyBackup()
{
    try
    {
        if (!File.Exists(file)) return;
        var name = $"data-{DateTime.Now:yyyy-MM-dd}.json";
        var today = Path.Combine(bdir, name);
        if (!File.Exists(today) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(today)) File.Copy(file, today, true);
        foreach (var old in Directory.GetFiles(bdir, "data-*.json").OrderByDescending(f => f).Skip(30)) File.Delete(old);
        if (backupExtra != "")
        {
            try
            {
                Directory.CreateDirectory(backupExtra);
                var ex = Path.Combine(backupExtra, name);
                if (!File.Exists(ex) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(ex)) File.Copy(file, ex, true);
                foreach (var old in Directory.GetFiles(backupExtra, "data-*.json").OrderByDescending(f => f).Skip(30)) File.Delete(old);
                backupExtraLast = DateTime.Now.ToString("o");
            }
            catch { }
        }
    }
    catch { }
}

// ---- Innstillinger for bilde-til-tekst (API-nøkkel og PIN til mobilsiden) ----
var cfgFile = Path.Combine(dir, "innstillinger.json");
string apiKey = "", pin = "", model = "claude-sonnet-5-5";
string cloudDir = "", cloudLast = ""; bool cloudAuto = false;
string mailAddr = "", mailPass = "", mailSmtp = "", mailImap = "", mailLast = "";
int mailPort = 0;
string mailSec = "auto", mailUser = "", mailFrom = "", mailFromName = "";
string syncId = "", syncPass = "", syncLast = ""; bool syncAuto = false, syncImgs = false;
void LoadCfg()
{
    try
    {
        if (File.Exists(cfgFile))
        {
            using var d = JsonDocument.Parse(File.ReadAllText(cfgFile));
            var r = d.RootElement;
            if (r.TryGetProperty("apiKey", out var k)) apiKey = k.GetString() ?? "";
            if (r.TryGetProperty("pin", out var p)) pin = p.GetString() ?? "";
            if (r.TryGetProperty("model", out var m) && !string.IsNullOrWhiteSpace(m.GetString())) model = m.GetString()!;
            if (r.TryGetProperty("cloudDir", out var cd) && cd.ValueKind == JsonValueKind.String) cloudDir = cd.GetString() ?? "";
            if (r.TryGetProperty("cloudLast", out var cl) && cl.ValueKind == JsonValueKind.String) cloudLast = cl.GetString() ?? "";
            if (r.TryGetProperty("backupExtra", out var be) && be.ValueKind == JsonValueKind.String) backupExtra = be.GetString() ?? "";
            if (r.TryGetProperty("cloudAuto", out var ca) && (ca.ValueKind == JsonValueKind.True || ca.ValueKind == JsonValueKind.False)) cloudAuto = ca.GetBoolean();
            if (r.TryGetProperty("mailAddr", out var ma) && ma.ValueKind == JsonValueKind.String) mailAddr = ma.GetString() ?? "";
            if (r.TryGetProperty("mailPass", out var mp) && mp.ValueKind == JsonValueKind.String) mailPass = mp.GetString() ?? "";
            if (r.TryGetProperty("mailPort", out var mpt) && mpt.ValueKind == JsonValueKind.Number) mailPort = mpt.GetInt32();
            if (r.TryGetProperty("mailSec", out var msc) && msc.ValueKind == JsonValueKind.String) mailSec = msc.GetString() ?? "auto";
            if (r.TryGetProperty("mailUser", out var mus) && mus.ValueKind == JsonValueKind.String) mailUser = mus.GetString() ?? "";
            if (r.TryGetProperty("mailFrom", out var mfr) && mfr.ValueKind == JsonValueKind.String) mailFrom = mfr.GetString() ?? "";
            if (r.TryGetProperty("mailFromName", out var mfn) && mfn.ValueKind == JsonValueKind.String) mailFromName = mfn.GetString() ?? "";
            if (r.TryGetProperty("mailSmtp", out var ms) && ms.ValueKind == JsonValueKind.String) mailSmtp = ms.GetString() ?? "";
            if (r.TryGetProperty("mailImap", out var mi) && mi.ValueKind == JsonValueKind.String) mailImap = mi.GetString() ?? "";
            if (r.TryGetProperty("mailLast", out var ml) && ml.ValueKind == JsonValueKind.String) mailLast = ml.GetString() ?? "";
            if (r.TryGetProperty("syncId", out var si) && si.ValueKind == JsonValueKind.String) syncId = si.GetString() ?? "";
            if (r.TryGetProperty("syncPass", out var sp) && sp.ValueKind == JsonValueKind.String) syncPass = sp.GetString() ?? "";
            if (r.TryGetProperty("syncLast", out var sl) && sl.ValueKind == JsonValueKind.String) syncLast = sl.GetString() ?? "";
            if (r.TryGetProperty("syncAuto", out var sa) && (sa.ValueKind == JsonValueKind.True || sa.ValueKind == JsonValueKind.False)) syncAuto = sa.GetBoolean();
            if (r.TryGetProperty("syncImgs", out var sg) && (sg.ValueKind == JsonValueKind.True || sg.ValueKind == JsonValueKind.False)) syncImgs = sg.GetBoolean();
        }
    }
    catch { }
    if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
}
void SaveCfg() => File.WriteAllText(cfgFile, JsonSerializer.Serialize(new { port = cfgPort, apiKey, pin, model, cloudDir, cloudLast, cloudAuto, backupExtra, mailAddr, mailPass, mailSmtp, mailImap, mailLast, mailPort, mailSec, mailUser, mailFrom, mailFromName, syncId, syncPass, syncLast, syncAuto, syncImgs }));
LoadCfg();
DailyBackup();
var backupTimer = new System.Threading.Timer(_ => DailyBackup(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

// ---- Utkast: sanger som er tatt bilde av og venter på kontroll ----
var draftFile = Path.Combine(dir, "utkast.json");
var draftLock = new object();
JsonArray LoadDrafts()
{
    try { if (File.Exists(draftFile)) return JsonNode.Parse(File.ReadAllText(draftFile))?.AsArray() ?? new JsonArray(); }
    catch { }
    return new JsonArray();
}
void SaveDrafts(JsonArray a) => File.WriteAllText(draftFile, a.ToJsonString());
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

bool IsLocal(HttpContext c)
{
    var ip = c.Connection.RemoteIpAddress;
    if (ip == null) return true;
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
    if (IPAddress.IsLoopback(ip)) return true;
    var l = c.Connection.LocalIpAddress;
    if (l != null) { if (l.IsIPv4MappedToIPv6) l = l.MapToIPv4(); if (ip.Equals(l)) return true; }
    return false;
}

bool PinOk(HttpContext c) => IsLocal(c) || string.IsNullOrEmpty(pin) || c.Request.Headers["X-Pin"].ToString() == pin;

app.Use(async (ctx, next) =>
{
    if (!IsLocal(ctx))
    {
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        var get = ctx.Request.Method == "GET";
        var post = ctx.Request.Method == "POST";
        var ok = (get && path.StartsWith("/api/slides/", StringComparison.OrdinalIgnoreCase)) || (get && (path.Equals("/stage.webmanifest", StringComparison.OrdinalIgnoreCase) || path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) || path.Equals("/nosleep.js", StringComparison.OrdinalIgnoreCase) || path.Equals("/app.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/sangvisning.apk", StringComparison.OrdinalIgnoreCase) || path.Equals("/stage.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/output.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/skjerm", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/view/stream", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/stream", StringComparison.OrdinalIgnoreCase) || path.Equals("/add.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/menu.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/remote.html", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/icon-", StringComparison.OrdinalIgnoreCase) || path.Equals("/icon.svg", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/remote/plan", StringComparison.OrdinalIgnoreCase)))
              || (post && (path.Equals("/api/ocr", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/drafts", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/remote", StringComparison.OrdinalIgnoreCase)));
        if (path == "" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/stage", StringComparison.OrdinalIgnoreCase) || path.Equals("/sanger", StringComparison.OrdinalIgnoreCase)) { ctx.Response.Redirect("/menu.html"); return; }
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 204; return; }
        if (!ok)
        {
            Console.WriteLine($"Blokkert: {ctx.Connection.RemoteIpAddress} ba om {ctx.Request.Method} {ctx.Request.Path}");
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync($"Kun tilgjengelig på maskinen der programmet kjører. Du ba om: {ctx.Request.Path}. Sangerskjermen ligger på /stage.html");
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
var ctProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
ctProvider.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = ctProvider,
    // Siden skal alltid sjekkes på nytt mot programmet, så en oppdatering vises med en gang.
    OnPrepareResponse = ctx => { ctx.Context.Response.Headers["Cache-Control"] = "no-cache"; }
});

app.MapGet("/api/data", () =>
    Results.Text(File.Exists(file) ? File.ReadAllText(file) : "{}", "application/json"));

app.MapPut("/api/data", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    DailyBackup(); // dagens kopi lages fra tilstanden før første lagring i dag
    if (File.Exists(file)) File.Copy(file, file + ".bak", true);
    await File.WriteAllTextAsync(file, body);
    return Results.Ok();
});

app.MapGet("/api/backups", () =>
    Results.Json(Directory.GetFiles(bdir, "data-*.json").OrderByDescending(f => f)
        .Select(f => new { name = Path.GetFileName(f), size = new FileInfo(f).Length })));

app.MapGet("/api/backups/{name}", (string name) =>
    nameRx.IsMatch(name) && File.Exists(Path.Combine(bdir, name))
        ? Results.Text(File.ReadAllText(Path.Combine(bdir, name)), "application/json")
        : Results.NotFound());

app.MapPost("/api/openbackups", () =>
{
    try { Process.Start(new ProcessStartInfo(bdir) { UseShellExecute = true }); return Results.Ok(); }
    catch { return Results.StatusCode(500); }
});

// ---- Bilde til tekst (sendes til Claude via API) ----
const string ocrPrompt = "Dette er et bilde av en side i en sangbok. Skriv av sangteksten nøyaktig slik den står, på originalspråket (vanligvis norsk). " +
    "Ta ikke med akkorder, noter, sidetall, copyright-linjer eller annen tekst som ikke er selve sangteksten. Behold linjeskiftene fra boka. " +
    "Sett en tom linje mellom hvert vers og refreng. Hvis versene er nummerert eller refrenget er merket, skriv en linje [Vers 1], [Vers 2], [Refreng] osv. rett før hver del, og fjern selve nummeret fra teksten. " +
    "Skriv først disse linjene (hopp over de som ikke finnes): TITTEL: ..., NUMMER: (sangens nummer i boka), FORFATTER: (tekst og melodi, hvis oppgitt). " +
    "Deretter en tom linje og sangteksten. Ikke legg til kommentarer eller forklaringer. Hvis noe er uleselig, skriv [uleselig].";

app.MapPost("/api/ocr", async (HttpContext ctx) =>
{
    if (!PinOk(ctx)) { await Task.Delay(800); return Results.Json(new { error = "Feil eller manglende PIN." }, statusCode: 401); }
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "Mangler API-nøkkel. Legg den inn under «Utkast» på kontrollsiden til programmet." }, statusCode: 501);
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    if (ms.Length == 0) return Results.Json(new { error = "Tomt bilde." }, statusCode: 400);
    var media = (ctx.Request.ContentType ?? "image/jpeg").Split(';')[0].Trim().ToLowerInvariant();
    if (media is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) media = "image/jpeg";
    var payload = JsonSerializer.Serialize(new
    {
        model,
        max_tokens = 4000,
        messages = new[]
        {
            new { role = "user", content = new object[]
            {
                new { type = "image", source = new { type = "base64", media_type = media, data = Convert.ToBase64String(ms.ToArray()) } },
                new { type = "text", text = ocrPrompt }
            } }
        }
    });
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = $"Feil fra tjenesten ({(int)resp.StatusCode}): " + (body.Length > 300 ? body[..300] : body) }, statusCode: 502);
        using var doc = JsonDocument.Parse(body);
        var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
            .Select(c => c.GetProperty("text").GetString()));
        string title = "", number = "", authors = "";
        var lines = text.Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0)
        {
            var m = Regex.Match(lines[0], @"^\s*(TITTEL|NUMMER|FORFATTER)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) break;
            var v = m.Groups[2].Value.Trim();
            switch (m.Groups[1].Value.ToUpperInvariant()) { case "TITTEL": title = v; break; case "NUMMER": number = v; break; default: authors = v; break; }
            lines.RemoveAt(0);
        }
        return Results.Json(new { title, number, authors, text = string.Join("\n", lines).Trim() });
    }
    catch (Exception e)
    {
        return Results.Json(new { error = "Fikk ikke kontakt med tjenesten (er det internett?): " + e.Message }, statusCode: 502);
    }
});

app.MapPost("/api/drafts", async (HttpContext ctx) =>
{
    if (!PinOk(ctx)) { await Task.Delay(800); return Results.Json(new { error = "Feil eller manglende PIN." }, statusCode: 401); }
    using var r = new StreamReader(ctx.Request.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject node) return Results.BadRequest();
    node["id"] = Guid.NewGuid().ToString("N");
    node["created"] = DateTime.Now.ToString("s");
    lock (draftLock) { var a = LoadDrafts(); a.Add(node); SaveDrafts(a); }
    return Results.Ok();
});

app.MapGet("/api/drafts", () => { lock (draftLock) return Results.Text(LoadDrafts().ToJsonString(), "application/json"); });

app.MapDelete("/api/drafts/{id}", (string id) =>
{
    lock (draftLock)
    {
        var keep = new JsonArray();
        foreach (var n in LoadDrafts()) if (n?["id"]?.GetValue<string>() != id) keep.Add(n?.DeepClone());
        SaveDrafts(keep);
    }
    return Results.Ok();
});

app.MapGet("/api/settings", () => Results.Json(new { hasKey = !string.IsNullOrWhiteSpace(apiKey), pin, model, port = cfgPort, activePort = port }));

app.MapPut("/api/settings", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject o) return Results.BadRequest();
    var k = o["apiKey"]?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(k)) apiKey = k.Trim();
    var p = o["pin"]?.GetValue<string>(); if (p != null) pin = p.Trim();
    var m = o["model"]?.GetValue<string>(); if (!string.IsNullOrWhiteSpace(m)) model = m.Trim();
    if (o["port"] is JsonNode pn)
    {
        int np; try { np = pn.GetValue<int>(); } catch { return Results.BadRequest("Porten må være et tall."); }
        if (np < 1024 || np > 65535) return Results.BadRequest("Porten må være mellom 1024 og 65535.");
        cfgPort = np;
    }
    SaveCfg();
    return Results.Ok();
});

// ---- Sangerskjerm: sender det som vises til alle tilkoblede mobiler (Server-Sent Events) ----
var clients = new List<Channel<string>>();
var lastLive = "{\"text\":\"\",\"blank\":true}";

app.MapPost("/api/live", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    lastLive = body;
    lock (clients) foreach (var c in clients) c.Writer.TryWrite(body);
    return Results.Ok();
});

app.MapGet("/api/stream", async (HttpContext ctx) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    var ch = Channel.CreateUnbounded<string>();
    lock (clients) clients.Add(ch);
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync("data: " + lastLive + "\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15000);
            try
            {
                var msg = await ch.Reader.ReadAsync(cts.Token);
                await ctx.Response.WriteAsync("data: " + msg + "\n\n", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct); // holder forbindelsen i live
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally { lock (clients) clients.Remove(ch); }
});

// ---- Lysbilder: bilder av presentasjoner, lagres på maskinen ----
var slideDir = Path.Combine(dir, "lysbilder");
static bool SafeId(string s) => s.Length > 0 && s.Length <= 64 && s.All(c => char.IsLetterOrDigit(c) || c == '-');

app.MapPost("/api/slides/{id}/{n:int}", async (string id, int n, HttpRequest req) =>
{
    if (!SafeId(id) || n < 0 || n > 999) return Results.BadRequest();
    var d = Path.Combine(slideDir, id); Directory.CreateDirectory(d);
    using var ms = new MemoryStream(); await req.Body.CopyToAsync(ms);
    await File.WriteAllBytesAsync(Path.Combine(d, n + ".jpg"), ms.ToArray());
    return Results.Ok();
});

app.MapGet("/api/slides/{id}/{n:int}", (string id, int n) =>
{
    if (!SafeId(id) || n < 0 || n > 999) return Results.BadRequest();
    var f = Path.Combine(slideDir, id, n + ".jpg");
    return File.Exists(f) ? Results.File(f, "image/jpeg") : Results.NotFound();
});

app.MapDelete("/api/slides/{id}", (string id) =>
{
    if (!SafeId(id)) return Results.BadRequest();
    var d = Path.Combine(slideDir, id);
    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    return Results.Ok();
});

// ---- Sky: en kopi av alt ligger i en mappe som Dropbox / Google Drive / OneDrive synkroniserer ----
// Programmet logger ikke inn noe sted selv. Det skriver bare en fil til en vanlig mappe, og sky-programmet på PC-en flytter den videre.
List<(string name, string path)> CloudCandidates()
{
    var l = new List<(string name, string path)>();
    void Add(string n, string? p)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            if (l.Any(x => string.Equals(x.path, p, StringComparison.OrdinalIgnoreCase))) return;
            l.Add((n, p));
        }
        catch { }
    }
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    foreach (var b in new[] { Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("APPDATA") })
    {
        if (string.IsNullOrEmpty(b)) continue;
        var info = Path.Combine(b, "Dropbox", "info.json");
        try
        {
            if (!File.Exists(info)) continue;
            using var d = JsonDocument.Parse(File.ReadAllText(info));
            foreach (var acc in d.RootElement.EnumerateObject())
                if (acc.Value.ValueKind == JsonValueKind.Object && acc.Value.TryGetProperty("path", out var pp)) Add("Dropbox", pp.GetString());
        }
        catch { }
    }
    Add("Dropbox", Path.Combine(home, "Dropbox"));
    Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
    Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
    try { foreach (var dd in Directory.GetDirectories(home, "OneDrive*")) Add("OneDrive", dd); } catch { }
    Add("Google Drive", Path.Combine(home, "Google Drive"));
    foreach (var gn in new[] { "My Drive", "Min disk", "Mitt drev" }) Add("Google Drive", Path.Combine(home, gn));
    try
    {
        foreach (var dr in DriveInfo.GetDrives())
        {
            try
            {
                if (!dr.IsReady) continue;
                foreach (var n in new[] { "My Drive", "Min disk", "Mitt drev" }) Add("Google Drive", Path.Combine(dr.RootDirectory.FullName, n));
            }
            catch { }
        }
    }
    catch { }
    Add("iCloud Drive", Path.Combine(home, "iCloudDrive"));
    return l;
}

string CloudFile() => Path.Combine(cloudDir, "sangvisning-data.json");

object CloudStatus()
{
    string? mod = null; int songs = -1;
    try
    {
        if (cloudDir != "" && File.Exists(CloudFile()))
        {
            mod = File.GetLastWriteTimeUtc(CloudFile()).ToString("o");
            try { songs = JsonNode.Parse(File.ReadAllText(CloudFile()))?["songs"]?.AsArray().Count ?? -1; } catch { }
        }
    }
    catch { }
    return new { dir = cloudDir, auto = cloudAuto, last = cloudLast, file = mod, songs, candidates = CloudCandidates().Select(c => new { name = c.name, path = c.path }).ToList() };
}

static void CopyTree(string srcDir, string dstDir)
{
    if (!Directory.Exists(srcDir)) return;
    foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
    {
        var dst = Path.Combine(dstDir, Path.GetRelativePath(srcDir, f));
        if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(f).Length) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(f, dst, true);
    }
}

app.MapGet("/api/browse", (string? path) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = new List<object>();
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(homeDir)) roots.Add(new { name = "Min bruker (" + homeDir + ")", path = homeDir });
            foreach (var drv in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drv.IsReady) continue;
                    string label = "";
                    try { label = drv.VolumeLabel; } catch { }
                    roots.Add(new { name = drv.Name + (string.IsNullOrEmpty(label) ? "" : " (" + label + ")"), path = drv.RootDirectory.FullName });
                }
                catch { }
            }
            return Results.Json(new { path = "", parent = (string?)null, dirs = roots });
        }
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return Results.NotFound("Fant ikke mappen.");
        var up = Directory.GetParent(full)?.FullName ?? "";
        var list = new List<object>();
        foreach (var sd in Directory.GetDirectories(full).OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var at = File.GetAttributes(sd);
                if ((at & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new { name = Path.GetFileName(sd), path = sd });
            }
            catch { }
        }
        return Results.Json(new { path = full, parent = up, dirs = list });
    }
    catch (Exception e) { return Results.BadRequest("Kunne ikke vise mappen: " + e.Message); }
});

app.MapGet("/api/cloud", () => Results.Json(CloudStatus()));

app.MapPost("/api/cloud/dir", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject o) return Results.BadRequest("Ugyldig forespørsel.");
    if (o["auto"] is JsonNode a) cloudAuto = a.GetValue<bool>();
    var p = o["path"]?.GetValue<string>()?.Trim().Trim('"');
    if (p != null)
    {
        if (p == "") cloudDir = "";
        else
        {
            try
            {
                if (!Directory.Exists(p)) return Results.BadRequest("Fant ikke mappen «" + p + "».");
                var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                var sub = string.Equals(name, "Sangvisning", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, "Sangvisning");
                Directory.CreateDirectory(sub);
                var testFile = Path.Combine(sub, ".skrivetest");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
                cloudDir = sub;
            }
            catch (Exception e) { return Results.BadRequest("Kunne ikke bruke mappen: " + e.Message); }
        }
    }
    SaveCfg();
    return Results.Json(CloudStatus());
});

app.MapPost("/api/cloud/push", async (HttpRequest req) =>
{
    if (cloudDir == "" || !Directory.Exists(cloudDir)) return Results.BadRequest("Sky-mappen er ikke valgt, eller finnes ikke lenger.");
    using var r = new StreamReader(req.Body);
    var body = await r.ReadToEndAsync();
    try
    {
        JsonNode.Parse(body);
        var f = CloudFile(); var tmp = f + ".tmp";
        if (File.Exists(f)) File.Copy(f, Path.Combine(cloudDir, "sangvisning-data.forrige.json"), true);
        await File.WriteAllTextAsync(tmp, body);
        File.Move(tmp, f, true);
        CopyTree(slideDir, Path.Combine(cloudDir, "lysbilder"));
        cloudLast = File.GetLastWriteTimeUtc(f).ToString("o");
        SaveCfg();
    }
    catch (Exception e) { return Results.BadRequest("Kunne ikke lagre til sky-mappen: " + e.Message); }
    return Results.Json(CloudStatus());
});

app.MapGet("/api/cloud/file", () =>
{
    if (cloudDir == "" || !File.Exists(CloudFile())) return Results.NotFound("Ingen kopi i skyen ennå.");
    try { return Results.Text(File.ReadAllText(CloudFile()), "application/json"); }
    catch (Exception e) { return Results.BadRequest("Kunne ikke lese: " + e.Message); }
});

app.MapPost("/api/cloud/pull", () =>
{
    if (cloudDir == "" || !File.Exists(CloudFile())) return Results.NotFound("Fant ingen kopi i sky-mappen ennå.");
    try
    {
        var text = File.ReadAllText(CloudFile());
        CopyTree(Path.Combine(cloudDir, "lysbilder"), slideDir);
        cloudLast = File.GetLastWriteTimeUtc(CloudFile()).ToString("o");
        SaveCfg();
        return Results.Text(text, "application/json");
    }
    catch (Exception e) { return Results.BadRequest("Kunne ikke lese fra sky-mappen: " + e.Message); }
});

// ---- E-post: send kopien som vedlegg til deg selv, og hent siste kopi fra innboksen ----
(string smtp, string imap) MailHosts()
{
    var dom = mailAddr.Contains('@') ? mailAddr.Substring(mailAddr.IndexOf('@') + 1).Trim().ToLowerInvariant() : "";
    string ds = "smtp." + dom, di = "imap." + dom;
    if (dom == "gmail.com" || dom == "googlemail.com") { ds = "smtp.gmail.com"; di = "imap.gmail.com"; }
    else if (dom == "outlook.com" || dom == "hotmail.com" || dom == "live.com" || dom == "live.no" || dom == "hotmail.no") { ds = "smtp-mail.outlook.com"; di = "outlook.office365.com"; }
    else if (dom == "icloud.com" || dom == "me.com") { ds = "smtp.mail.me.com"; di = "imap.mail.me.com"; }
    return (string.IsNullOrWhiteSpace(mailSmtp) ? ds : mailSmtp.Trim(), string.IsNullOrWhiteSpace(mailImap) ? di : mailImap.Trim());
}
SecureSocketOptions SmtpSec(int port) => mailSec switch
{
    "ssl" => SecureSocketOptions.SslOnConnect,
    "starttls" => SecureSocketOptions.StartTls,
    "none" => SecureSocketOptions.None,
    _ => port == 465 ? SecureSocketOptions.SslOnConnect : port == 25 ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.StartTls
};
bool MailReady() => mailAddr != "" && (mailPass != "" || mailSec == "none");
MailboxAddress MailFrom() => new MailboxAddress(mailFromName ?? "", string.IsNullOrWhiteSpace(mailFrom) ? mailAddr : mailFrom.Trim());
async Task SmtpSend(MimeMessage msg)
{
    var h = MailHosts();
    int port = mailPort > 0 ? mailPort : 587;
    using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 60000 };
    await smtp.ConnectAsync(h.smtp, port, SmtpSec(port));
    if (mailPass != "") await smtp.AuthenticateAsync(string.IsNullOrWhiteSpace(mailUser) ? mailAddr : mailUser.Trim(), mailPass);
    await smtp.SendAsync(msg);
    await smtp.DisconnectAsync(true);
}
object MailCfgOut()
{
    var h = MailHosts();
    return new { addr = mailAddr, hasPass = mailPass != "", smtp = h.smtp, imap = h.imap, last = mailLast, customSmtp = mailSmtp, customImap = mailImap, port = mailPort, sec = mailSec, user = mailUser, from = mailFrom, fromName = mailFromName };
}
string MailErr(Exception e)
{
    if (e is MailKit.Security.SslHandshakeException)
        return "Sikkerhetsvalget passer ikke til porten. Prøv port 587 med STARTTLS, eller port 465 med SSL/TLS. (" + e.Message + ")";
    if (e is MailKit.Security.AuthenticationException)
        return "Innloggingen ble avvist. Bruk et app-passord, ikke vanlig passord (for Gmail: slå på tofaktor og lag app-passord under Sikkerhet), og sjekk adressen.";
    if (e is MailKit.ServiceNotConnectedException || e is System.Net.Sockets.SocketException || e is TimeoutException || e is System.IO.IOException)
        return "Fikk ikke kontakt med e-postserveren (er PC-en på nett, og stemmer serveradressen?): " + e.Message;
    return "E-postfeil: " + e.Message;
}

app.MapGet("/api/mail/cfg", () =>
{
    return Results.Json(MailCfgOut());
});

app.MapPost("/api/mail/cfg", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject o) return Results.BadRequest("Ugyldig forespørsel.");
    if (o["addr"] is JsonNode n1) mailAddr = (n1.GetValue<string>() ?? "").Trim();
    if (o["pass"] is JsonNode n2 && !string.IsNullOrEmpty(n2.GetValue<string>())) mailPass = n2.GetValue<string>().Replace(" ", "");
    if (o["smtp"] is JsonNode n3) mailSmtp = (n3.GetValue<string>() ?? "").Trim();
    if (o["imap"] is JsonNode n4) mailImap = (n4.GetValue<string>() ?? "").Trim();
    if (o["port"] is JsonNode n5) mailPort = int.TryParse(n5.ToString(), out var pv) && pv > 0 && pv < 65536 ? pv : 0;
    if (o["sec"] is JsonNode n6) { var sv = (n6.GetValue<string>() ?? "auto").Trim().ToLowerInvariant(); mailSec = sv is "starttls" or "ssl" or "none" ? sv : "auto"; }
    if (o["user"] is JsonNode n7) mailUser = (n7.GetValue<string>() ?? "").Trim();
    if (o["from"] is JsonNode n8) mailFrom = (n8.GetValue<string>() ?? "").Trim();
    if (o["fromName"] is JsonNode n9) mailFromName = (n9.GetValue<string>() ?? "").Trim();
    SaveCfg();
    return Results.Json(MailCfgOut());
});

app.MapPost("/api/mail/send", async (HttpRequest req) =>
{
    if (!MailReady()) return Results.BadRequest("Sett opp e-post under Innstillinger → E-post først.");
    try
    {
        using var r = new StreamReader(req.Body);
        var json = await r.ReadToEndAsync();
        var msg = new MimeMessage();
        msg.From.Add(MailFrom());
        msg.To.Add(MailboxAddress.Parse(mailAddr));
        msg.Subject = "Sangvisning kopi " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var bb = new BodyBuilder { TextBody = "Kopi av sanger og møteplaner fra Sangvisning. Slett ikke denne e-posten hvis du vil hente den på en annen PC." };
        bb.Attachments.Add("sangvisning-kopi.json", Encoding.UTF8.GetBytes(json), new ContentType("application", "json"));
        msg.Body = bb.ToMessageBody();
        await SmtpSend(msg);
        mailLast = DateTime.UtcNow.ToString("o");
        SaveCfg();
        return Results.Json(new { last = mailLast });
    }
    catch (Exception e) { return Results.BadRequest(MailErr(e)); }
});

app.MapPost("/api/mail/plan", async (HttpRequest req) =>
{
    if (!MailReady()) return Results.BadRequest("Sett opp e-post under Innstillinger → E-post først.");
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var o = doc.RootElement;
        string Get(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var msg = new MimeMessage();
        msg.From.Add(MailFrom());
        var n = 0;
        foreach (var a in Get("to").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) { msg.To.Add(MailboxAddress.Parse(a.Trim())); n++; }
        if (n == 0) return Results.BadRequest("Skriv inn minst én e-postadresse.");
        msg.Subject = Get("subject") == "" ? "Sanger" : Get("subject");
        var bb = new BodyBuilder { TextBody = Get("text") };
        if (Get("html") != "") bb.HtmlBody = Get("html");
        if (Get("pdf") != "") bb.Attachments.Add(Get("pdfName") == "" ? "sanger.pdf" : Get("pdfName"), Convert.FromBase64String(Get("pdf")), new ContentType("application", "pdf"));
        msg.Body = bb.ToMessageBody();
        await SmtpSend(msg);
        return Results.Json(new { ok = true });
    }
    catch (Exception e) { return Results.BadRequest(MailErr(e)); }
});

app.MapPost("/api/mail/test", async () =>
{
    if (!MailReady()) return Results.BadRequest("Fyll inn og lagre e-postoppsettet først.");
    try
    {
        var msg = new MimeMessage();
        msg.From.Add(MailFrom());
        msg.To.Add(MailboxAddress.Parse(mailAddr));
        msg.Subject = "Test fra Sangvisning";
        msg.Body = new TextPart("plain") { Text = "Dette er en testmelding fra Sangvisning. E-postoppsettet fungerer." };
        await SmtpSend(msg);
        return Results.Json(new { ok = true });
    }
    catch (Exception e) { return Results.BadRequest(MailErr(e)); }
});

app.MapGet("/api/mail/fetch", async () =>
{
    if (mailAddr == "" || mailPass == "") return Results.BadRequest("Fyll inn e-postadresse og app-passord først.");
    try
    {
        var h = MailHosts();
        using var imap = new ImapClient { Timeout = 60000 };
        await imap.ConnectAsync(h.imap, 993, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(mailAddr, mailPass);
        var inbox = imap.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var ids = await inbox.SearchAsync(SearchQuery.SubjectContains("Sangvisning kopi"));
        if (ids.Count == 0) return Results.NotFound("Fant ingen e-post med en Sangvisning-kopi i innboksen.");
        var sums = await inbox.FetchAsync(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate);
        foreach (var s in sums.OrderByDescending(x => x.InternalDate ?? DateTimeOffset.MinValue))
        {
            var m = await inbox.GetMessageAsync(s.UniqueId);
            foreach (var part in m.Attachments.OfType<MimePart>())
            {
                if (!(part.FileName ?? "").EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms);
                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                await imap.DisconnectAsync(true);
                return Results.Json(new { date = (s.InternalDate ?? DateTimeOffset.UtcNow).ToString("o"), data = node });
            }
        }
        return Results.NotFound("Fant e-poster, men ingen med vedlegget sangvisning-kopi.json.");
    }
    catch (Exception e) { return Results.BadRequest(MailErr(e)); }
});

// ---- Synkkode: en kryptert kopi på en anonym lagringsplass (jsonblob.com). Krypteringen skjer i nettleseren, tjenesten ser bare tilfeldig tekst. ----
async Task<IResult> BlobCall(HttpMethod m, string? id, string? body)
{
    try
    {
        var uri = "https://jsonblob.com/api/jsonBlob" + (id == null ? "" : "/" + id);
        using var msg = new HttpRequestMessage(m, uri);
        msg.Headers.Accept.ParseAdd("application/json");
        msg.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        if (body != null) msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(msg);
        if (resp.StatusCode == HttpStatusCode.NotFound) return Results.NotFound("Fant ingen data hos tjenesten.");
        if (!resp.IsSuccessStatusCode) {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(); } catch { }
            if (errBody.Length > 200) errBody = errBody.Substring(0, 200);
            return Results.BadRequest("Tjenesten svarte " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ". " + errBody);
        }
        if (m == HttpMethod.Post)
        {
            var loc = resp.Headers.Location?.ToString() ?? "";
            var nid = loc.TrimEnd('/').Split('/').Last();
            if (!SafeId(nid)) return Results.BadRequest("Fikk ikke en gyldig kode fra tjenesten.");
            return Results.Json(new { id = nid });
        }
        return Results.Text(await resp.Content.ReadAsStringAsync(), "application/json");
    }
    catch (Exception e) { return Results.BadRequest("Fikk ikke kontakt med tjenesten (er PC-en på nett?): " + e.Message); }
}

app.MapGet("/api/sync/cfg", () => Results.Json(new { id = syncId, pass = syncPass, last = syncLast, auto = syncAuto, imgs = syncImgs }));

app.MapPost("/api/sync/cfg", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject o) return Results.BadRequest("Ugyldig forespørsel.");
    if (o["id"] is JsonNode n1) syncId = n1.GetValue<string>();
    if (o["pass"] is JsonNode n2) syncPass = n2.GetValue<string>();
    if (o["last"] is JsonNode n3) syncLast = n3.GetValue<string>();
    if (o["auto"] is JsonNode n4) syncAuto = n4.GetValue<bool>();
    if (o["imgs"] is JsonNode n5) syncImgs = n5.GetValue<bool>();
    SaveCfg();
    return Results.Json(new { id = syncId, pass = syncPass, last = syncLast, auto = syncAuto, imgs = syncImgs });
});

app.MapPost("/api/sync/create", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    return await BlobCall(HttpMethod.Post, null, await r.ReadToEndAsync());
});

app.MapGet("/api/sync/blob/{id}", async (string id) =>
    SafeId(id) ? await BlobCall(HttpMethod.Get, id, null) : Results.BadRequest("Ugyldig kode."));

app.MapPut("/api/sync/blob/{id}", async (string id, HttpRequest req) =>
{
    if (!SafeId(id)) return Results.BadRequest("Ugyldig kode.");
    using var r = new StreamReader(req.Body);
    return await BlobCall(HttpMethod.Put, id, await r.ReadToEndAsync());
});

// ---- Projektorvindu: eget Chrome/Edge-vindu i fullskjerm på valgt skjerm, med egen profil. Videresender meldinger mellom kontrollvinduet og projektorvinduet. ----
Process? outProc = null;
var toOutClients = new List<Channel<string>>();
var fromOutClients = new List<Channel<string>>();

app.MapPost("/api/output/open", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    int.TryParse(ctx.Request.Query["x"], out var px);
    int.TryParse(ctx.Request.Query["y"], out var py);
    try { if (outProc != null && !outProc.HasExited) outProc.Kill(true); } catch { }
    try { Directory.Delete(Path.Combine(dir, "utgang-profil"), true); } catch { }
    var prof = Path.Combine(dir, "utgang-profil", DateTime.Now.Ticks.ToString());
    var args = $"--kiosk --app={url}/output.html?relay=1 --user-data-dir=\"{prof}\" --window-position={px},{py} --no-first-run --no-default-browser-check --autoplay-policy=no-user-gesture-required --disable-infobars --disable-session-crashed-bubble --disable-features=Translate";
    foreach (var nl in new[] { "chrome", "msedge" })
    {
        try
        {
            outProc = Process.Start(new ProcessStartInfo(nl, args) { UseShellExecute = true });
            if (outProc != null) return Results.Ok();
        }
        catch { }
    }
    return Results.BadRequest("Fant ikke Chrome eller Edge.");
});

app.MapPost("/api/output/close", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    lock (toOutClients) foreach (var cl in toOutClients) cl.Writer.TryWrite("{\"type\":\"closeOut\"}");
    await Task.Delay(500);
    try { if (outProc != null && !outProc.HasExited) outProc.Kill(true); } catch { }
    outProc = null;
    return Results.Ok();
});

async Task RelayStream(HttpContext ctx, List<Channel<string>> list)
{
    if (!IsLocal(ctx)) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    var chn = Channel.CreateUnbounded<string>();
    lock (list) list.Add(chn);
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync(": hei\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15000);
            try { var msg = await chn.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync("data: " + msg + "\n\n", ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await ctx.Response.WriteAsync(": ping\n\n", ct); }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally { lock (list) list.Remove(chn); }
}

async Task<IResult> RelayPost(HttpContext ctx, List<Channel<string>> list)
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    using var rd = new StreamReader(ctx.Request.Body);
    var body = await rd.ReadToEndAsync();
    lock (list) foreach (var cl in list) cl.Writer.TryWrite(body.Replace("\n", " ").Replace("\r", " "));
    return Results.Ok();
}

app.MapGet("/api/relay/to-output", (HttpContext ctx) => RelayStream(ctx, toOutClients));
app.MapPost("/api/relay/to-output", (HttpContext ctx) => RelayPost(ctx, toOutClients));
app.MapGet("/api/relay/from-output", (HttpContext ctx) => RelayStream(ctx, fromOutClients));
app.MapPost("/api/relay/from-output", (HttpContext ctx) => RelayPost(ctx, fromOutClients));

// ---- Lydfiler (spilles i kontrollsiden) ----
var adir = Path.Combine(dir, "Lyd");
Directory.CreateDirectory(adir);
var audioTypes = new Dictionary<string, string> { [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".oga"] = "audio/ogg", [".opus"] = "audio/ogg", [".flac"] = "audio/flac", [".weba"] = "audio/webm" };

app.MapPost("/api/audio", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    var ext = Path.GetExtension(ctx.Request.Query["name"].ToString()).ToLowerInvariant();
    if (!audioTypes.ContainsKey(ext)) return Results.BadRequest("Filtypen støttes ikke. Bruk mp3, m4a, wav, ogg eller flac.");
    var mx = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (mx != null && !mx.IsReadOnly) mx.MaxRequestBodySize = null;
    var name = Guid.NewGuid().ToString("N") + ext;
    var path = Path.Combine(adir, name);
    try { using var fs = File.Create(path); await ctx.Request.Body.CopyToAsync(fs); }
    catch (Exception e) { try { File.Delete(path); } catch { } return Results.BadRequest("Kunne ikke lagre lydfilen: " + e.Message); }
    return Results.Json(new { file = name, size = new FileInfo(path).Length });
});

app.MapGet("/api/audio/{name}", (HttpContext ctx, string name) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    if (!Regex.IsMatch(name, @"^[0-9a-f]{32}\.[a-z0-9]{2,5}$")) return Results.NotFound();
    var p = Path.Combine(adir, name);
    if (!File.Exists(p) || !audioTypes.TryGetValue(Path.GetExtension(p).ToLowerInvariant(), out var ct)) return Results.NotFound();
    return Results.File(p, ct, enableRangeProcessing: true);
});

// ---- Skjermvisning i nettleser (en ekstra skjerm: TV, nettbrett, annen PC på samme nettverk) ----
var viewClients = new List<Channel<string>>();
var viewState = new Dictionary<string, string>();
string[] viewOrder = { "theme", "blackout", "img", "text", "msg", "cd" };

app.MapGet("/skjerm", (HttpContext ctx) => { ctx.Response.Redirect("/output.html?view=1"); return Task.CompletedTask; });

app.MapPost("/api/view/post", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    using var rd = new StreamReader(ctx.Request.Body);
    var body = (await rd.ReadToEndAsync()).Replace("\n", " ").Replace("\r", " ");
    try
    {
        if (JsonNode.Parse(body) is JsonObject o)
        {
            var type = o["type"]?.GetValue<string>() ?? "text";
            lock (viewState)
            {
                if (type == "msgoff") viewState.Remove("msg");
                else if (type == "cdoff") viewState.Remove("cd");
                else if (Array.IndexOf(viewOrder, type) >= 0) viewState[type] = body;
            }
        }
    }
    catch { return Results.BadRequest(); }
    lock (viewClients) foreach (var cl in viewClients) cl.Writer.TryWrite(body);
    return Results.Ok();
});

app.MapGet("/api/view/stream", async (HttpContext ctx) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    var chn = Channel.CreateUnbounded<string>();
    lock (viewState) foreach (var k in viewOrder) if (viewState.TryGetValue(k, out var s)) chn.Writer.TryWrite(s);
    lock (viewClients) viewClients.Add(chn);
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync(": hei\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var m = await chn.Reader.ReadAsync(cts.Token);
                await ctx.Response.WriteAsync("data: " + m + "\n\n", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally { lock (viewClients) viewClients.Remove(chn); }
});

// ---- Ekstra mappe for automatisk sikkerhetskopi (f.eks. Google Drive) ----
app.MapGet("/api/backup/extra", () => Results.Json(new { dir = backupExtra, last = backupExtraLast }));

app.MapPost("/api/backup/extra", async (HttpRequest req) =>
{
    using var r = new StreamReader(req.Body);
    if (JsonNode.Parse(await r.ReadToEndAsync()) is not JsonObject o) return Results.BadRequest("Ugyldig forespørsel.");
    var d = (o["dir"]?.GetValue<string>() ?? "").Trim();
    if (d != "")
    {
        try
        {
            d = Path.GetFullPath(d);
            Directory.CreateDirectory(d);
            var t = Path.Combine(d, ".skrivetest");
            File.WriteAllText(t, "ok");
            File.Delete(t);
        }
        catch (Exception e) { return Results.BadRequest("Kan ikke skrive til mappen: " + e.Message); }
    }
    backupExtra = d;
    SaveCfg();
    DailyBackup();
    return Results.Json(new { dir = backupExtra, last = backupExtraLast });
});

// ---- Fjernstyring: mobilen velger sang/vers fra møteplanen ----
var ctrlClients = new List<Channel<string>>();
var planInfo = "{\"items\":[]}";

app.MapPost("/api/planinfo", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    planInfo = await reader.ReadToEndAsync();
    return Results.Ok();
});

app.MapGet("/api/remote/plan", () => Results.Text(planInfo, "application/json"));

app.MapPost("/api/remote", async (HttpContext ctx) =>
{
    if (!PinOk(ctx)) { await Task.Delay(800); return Results.Json(new { error = "Feil eller manglende PIN." }, statusCode: 401); }
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    lock (ctrlClients) foreach (var c in ctrlClients) c.Writer.TryWrite(body);
    return Results.Ok();
});

app.MapGet("/api/control", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    var ch = Channel.CreateUnbounded<string>();
    lock (ctrlClients) ctrlClients.Add(ch);
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync(": hei\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15000);
            try { var msg = await ch.Reader.ReadAsync(cts.Token); await ctx.Response.WriteAsync("data: " + msg + "\n\n", ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await ctx.Response.WriteAsync(": ping\n\n", ct); }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally { lock (ctrlClients) ctrlClients.Remove(ch); }
});

app.MapGet("/api/info", () =>
{
    var ips = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n.Name + " " + n.Description, "VMware|VirtualBox|Hyper-V|vEthernet|WSL|Virtual|VPN|TAP|Tailscale|ZeroTier", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString())
        .Where(a => !a.StartsWith("169.254."))
        .Distinct().ToArray();
    return Results.Json(new { port, lan, ips });
});

// --- Oppdatering fra GitHub (virker bare for lokal bruker) ---
var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
bool installert = (Environment.ProcessPath ?? "").Contains(Path.Combine("Programs", "Sangvisning"), StringComparison.OrdinalIgnoreCase);
async Task<(string tag, string assetUrl, string page)?> SisteUtgave()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Sangvisning-oppdatering");
    var js = await http.GetStringAsync("https://api.github.com/repos/oddlosnes-beep/sangvisning/releases/latest");
    using var d = JsonDocument.Parse(js);
    var r = d.RootElement;
    var tag = r.GetProperty("tag_name").GetString() ?? "";
    var asset = "";
    foreach (var a in r.GetProperty("assets").EnumerateArray())
        if ((a.GetProperty("name").GetString() ?? "") == "Sangvisning-Setup.exe") asset = a.GetProperty("browser_download_url").GetString() ?? "";
    return (tag, asset, r.GetProperty("html_url").GetString() ?? "");
}
app.MapGet("/api/version", (HttpContext ctx) => IsLocal(ctx) ? Results.Json(new { version = appVersion.ToString(3), installed = installert, dataDir = dir }) : Results.StatusCode(403));
app.MapGet("/api/update/check", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    try
    {
        var u = await SisteUtgave();
        if (u is null) throw new Exception("tom");
        var ok = Version.TryParse(u.Value.tag.TrimStart('v', 'V'), out var lv);
        return Results.Json(new { current = appVersion.ToString(3), latest = ok ? lv!.ToString(3) : "", newer = ok && lv! > appVersion, canInstall = installert && u.Value.assetUrl != "", page = u.Value.page, error = false });
    }
    catch
    {
        return Results.Json(new { current = appVersion.ToString(3), latest = "", newer = false, canInstall = false, page = "", error = true });
    }
});
string updStage = "idle", updErr = "";
long updDone = 0, updTotal = 0;
app.MapGet("/api/update/status", (HttpContext ctx) => IsLocal(ctx) ? Results.Json(new { stage = updStage, done = updDone, total = updTotal, error = updErr }) : Results.StatusCode(403));
app.MapPost("/api/update/install", async (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    if (!installert) return Results.BadRequest("Oppdatering fra programmet virker bare i den installerte versjonen.");
    if (updStage == "download" || updStage == "install") return Results.Ok();
    updStage = "download"; updDone = 0; updTotal = 0; updErr = "";
    _ = Task.Run(async () =>
    {
        try
        {
            var u = await SisteUtgave();
            var dl = u?.assetUrl ?? "";
            if (!dl.StartsWith("https://github.com/oddlosnes-beep/sangvisning/releases/download/")) throw new Exception("Fant ikke installasjonsfilen.");
            var tmp = Path.Combine(Path.GetTempPath(), "Sangvisning-Setup-ny.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Sangvisning-oppdatering");
                using var resp = await http.GetAsync(dl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                updTotal = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var fs = File.Create(tmp);
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf)) > 0) { await fs.WriteAsync(buf.AsMemory(0, n)); updDone += n; }
            }
            if (new FileInfo(tmp).Length < 5_000_000) throw new Exception("Nedlastingen ble ufullstendig.");
            updStage = "install";
            var log = Path.Combine(Path.GetTempPath(), "Sangvisning-oppdatering.log");
            var pr = Process.Start(new ProcessStartInfo(tmp, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{log}\"") { UseShellExecute = true });
            if (pr != null)
            {
                await pr.WaitForExitAsync();
                // Kommer vi hit, ble ikke programmet stoppet av installasjonen: da gikk noe galt.
                if (pr.ExitCode != 0) { updErr = "Installasjonen avsluttet med feilkode " + pr.ExitCode + ". Logg: " + log; updStage = "error"; }
                else updStage = "done";
            }
        }
        catch (Exception e) { updErr = e.Message; updStage = "error"; }
    });
    return Results.Ok();
});

app.MapPost("/api/ytfs", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    return Results.Json(new { ok = OperatingSystem.IsWindows() && WinKill.FullscreenYouTube(ctx.Request.Query["tast"] == "f11") });
});

app.MapPost("/api/ytkey", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    var k = ctx.Request.Query["k"].ToString();
    byte vk = k == "left" ? (byte)0x25 : (byte)0x27;
    int n = int.TryParse(ctx.Request.Query["n"], out var nn) ? Math.Clamp(nn, 1, 12) : 1;
    return Results.Json(new { ok = OperatingSystem.IsWindows() && WinKill.SendKey(vk, n) });
});

app.MapPost("/api/ytclick", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    return Results.Json(new { ok = OperatingSystem.IsWindows() && WinKill.ClickCenter() });
});

app.MapPost("/api/closeyt", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    return Results.Json(new { closed = OperatingSystem.IsWindows() ? WinKill.CloseYouTube(ctx.Request.Query["alle"] == "1") : 0 });
});

app.MapPost("/api/quit", (HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Results.StatusCode(403);
    _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
    return Results.Ok();
});

// Svarer når Android-appen spør «hvor er Sangvisning?» på nettverket (UDP 45678).
if (lan) _ = Task.Run(async () =>
{
    try
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 45678));
        var svar = Encoding.UTF8.GetBytes($"SANGVISNING:{port}:{Environment.MachineName}");
        while (true)
        {
            var r = await udp.ReceiveAsync();
            if (Encoding.UTF8.GetString(r.Buffer).StartsWith("SANGVISNING?")) await udp.SendAsync(svar, svar.Length, r.RemoteEndPoint);
        }
    }
    catch { }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    try { File.WriteAllText(Path.Combine(appDataDir, "port.txt"), port.ToString()); } catch { }
    if (portNote != "") Console.WriteLine(portNote);
    Console.WriteLine($"Sangvisning kjører på {url}{(lan ? " (sangerskjerm også på nettverket)" : "")}\nData: {file}\nKopier: {bdir}\nLukk dette vinduet for å avslutte.");
    // Etter en oppdatering fra programmet er vinduet allerede åpent, så da åpnes det ikke et nytt.
    if (args.Contains("--ingen-vindu")) return;
    // Åpner som eget programvindu (uten adresselinje og faner): Chrome, ellers Edge, ellers vanlig nettleser.
    var adr = url + "/index.html";
    foreach (var nl in new[] { "chrome", "msedge" })
    {
        try { Process.Start(new ProcessStartInfo(nl, $"--app={adr} --start-maximized") { UseShellExecute = true }); return; } catch { }
    }
    try { Process.Start(new ProcessStartInfo(adr) { UseShellExecute = true }); } catch { }
});

app.Run();
GC.KeepAlive(backupTimer);

// Lukker YouTube-vinduet som Sangvisning åpnet. Nettleseren gir ikke siden lov til å lukke det selv.
static class WinKill
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static bool FullscreenYouTube(bool f11)
    {
        IntPtr yt = IntPtr.Zero, other = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            var cn = new StringBuilder(100); GetClassName(h, cn, 100);
            var t = sb.ToString();
            if (cn.ToString() != "Chrome_WidgetWin_1" || t.Length == 0 || t.Contains("Sangvisning") || t.Contains("Utgang") || t.Contains("Sangerskjerm")) return true;
            if (t.Contains("YouTube")) { yt = h; return false; }
            if (other == IntPtr.Zero) other = h; // øverste ellers (siden som nettopp ble åpnet)
            return true;
        }, IntPtr.Zero);
        var found = yt != IntPtr.Zero ? yt : other;
        if (found == IntPtr.Zero) return false;
        ShowWindow(found, 9); SetForegroundWindow(found);
        Thread.Sleep(250);
        byte vk = f11 ? (byte)0x7A : (byte)0x46; // F11 = nettleser-fullskjerm, F = fullskjerm i spilleren
        keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero);
        return true;
    }

    public static bool SendKey(byte vk, int times)
    {
        IntPtr yt = IntPtr.Zero, other = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            var cn = new StringBuilder(100); GetClassName(h, cn, 100);
            var t = sb.ToString();
            if (cn.ToString() != "Chrome_WidgetWin_1" || t.Length == 0 || t.Contains("Sangvisning") || t.Contains("Utgang") || t.Contains("Sangerskjerm")) return true;
            if (t.Contains("YouTube")) { yt = h; return false; }
            if (other == IntPtr.Zero) other = h;
            return true;
        }, IntPtr.Zero);
        var w = yt != IntPtr.Zero ? yt : other;
        if (w == IntPtr.Zero) return false;
        ShowWindow(w, 9); SetForegroundWindow(w);
        Thread.Sleep(200);
        for (int i = 0; i < times; i++) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); Thread.Sleep(60); }
        return true;
    }

    public static bool ClickCenter()
    {
        IntPtr yt = IntPtr.Zero, other = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            var cn = new StringBuilder(100); GetClassName(h, cn, 100);
            var t = sb.ToString();
            if (cn.ToString() != "Chrome_WidgetWin_1" || t.Length == 0 || t.Contains("Sangvisning") || t.Contains("Utgang") || t.Contains("Sangerskjerm")) return true;
            if (t.Contains("YouTube")) { yt = h; return false; }
            if (other == IntPtr.Zero) other = h;
            return true;
        }, IntPtr.Zero);
        var w = yt != IntPtr.Zero ? yt : other;
        if (w == IntPtr.Zero || !GetWindowRect(w, out var r)) return false;
        GetCursorPos(out var old);
        ShowWindow(w, 9); SetForegroundWindow(w);
        Thread.Sleep(200);
        SetCursorPos((r.L + r.R) / 2, (r.T + r.B) / 2 + 15);
        Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // venstreklikk midt i vinduet
        Thread.Sleep(120);
        SetCursorPos(old.X, old.Y);
        return true;
    }

    public static int CloseYouTube(bool any)
    {
        int n = 0; IntPtr other = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            var cn = new StringBuilder(100); GetClassName(h, cn, 100);
            var t = sb.ToString();
            if (cn.ToString() != "Chrome_WidgetWin_1" || t.Length == 0 || t.Contains("Sangvisning") || t.Contains("Utgang") || t.Contains("Sangerskjerm")) return true;
            if (t.Contains("YouTube")) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; }
            else if (other == IntPtr.Zero) other = h;
            return true;
        }, IntPtr.Zero);
        if (n == 0 && any && other != IntPtr.Zero) { PostMessage(other, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; }
        return n;
    }
}
