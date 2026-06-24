using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.ApplicationInsights;   // GetMetric extension on TelemetryClient (Exhibit #5)
using Azure;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Playground.App;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // wwwroot lives next to the binary
});
// Exhibit #5 — App Insights. Reads APPLICATIONINSIGHTS_CONNECTION_STRING from app
// settings (injected post-deploy); inert when unset. This is the only telemetry
// wiring in the whole app: dependency calls (SQL, Cosmos, the API hop, Service Bus,
// Event Grid) and the request graph are captured automatically, which is the whole
// point of the exhibit — the App Map draws itself from traffic, no agent install.
builder.Services.AddApplicationInsightsTelemetry();
var app = builder.Build();
// Telemetry client for the custom metrics/events the load generator emits.
var telemetry = app.Services.GetRequiredService<Microsoft.ApplicationInsights.TelemetryClient>();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Config (env-driven; empty = "not bolted on yet") ──────────────────────
var sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION") ?? "";
var apiBase = Environment.GetEnvironmentVariable("API_BASE") ?? "";
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "";
var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "";

var apiSecret = Environment.GetEnvironmentVariable("API_SHARED_SECRET") ?? "";

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
if (!string.IsNullOrWhiteSpace(apiBase)) http.BaseAddress = new Uri(apiBase);
if (!string.IsNullOrEmpty(apiSecret)) http.DefaultRequestHeaders.Add("X-Playground-Key", apiSecret);

// Direct (in-process) Cosmos client — gateway mode (App Service sandbox).
// `members` feeds the latency exhibit; `fanout` holds Exhibit #4's receipts.
Container? cosmosDirect = null;
Container? fanoutReceipts = null;
if (!string.IsNullOrWhiteSpace(cosmosEndpoint) && !string.IsNullOrWhiteSpace(cosmosKey))
{
    var cc = new CosmosClient(cosmosEndpoint, cosmosKey,
        new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
    cosmosDirect = cc.GetContainer("playground", "members");
    fanoutReceipts = cc.GetContainer("playground", "fanout");
}

// Exhibit #2: Service Bus client for the (ill-advised) sync request/reply path.
var sbConn = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION") ?? "";
ServiceBusClient? sbClient = !string.IsNullOrWhiteSpace(sbConn)
    ? new ServiceBusClient(sbConn, new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets })
    : null;
ServiceBusSender? sbRequests = sbClient?.CreateSender("reveal-requests");

// Exhibit #4 — fan-out publishers. The app sends one event down a chosen path;
// the consumers (in the Functions tier) each record a receipt the page reads back.
//   • Service Bus: a sender on the 'fanout' TOPIC (exists only on Standard / SB_TOPICS=1).
//   • Event Grid:  a publisher to the custom topic (endpoint + key injected post-deploy).
ServiceBusSender? sbFanout = sbClient?.CreateSender("fanout");

var egEndpoint = Environment.GetEnvironmentVariable("EVENTGRID_ENDPOINT") ?? "";
var egKey = Environment.GetEnvironmentVariable("EVENTGRID_KEY") ?? "";
EventGridPublisherClient? egClient =
    (!string.IsNullOrWhiteSpace(egEndpoint) && !string.IsNullOrWhiteSpace(egKey))
        ? new EventGridPublisherClient(new Uri(egEndpoint), new AzureKeyCredential(egKey))
        : null;

await Db.EnsureSqlSeed(sqlConn, app.Logger);

app.MapGet("/healthz", () => Results.Text("ok"));

// What's wired right now — drives the exhibit's "flip enableX" hints.
app.MapGet("/api/config", () => Results.Json(new
{
    sql = !string.IsNullOrWhiteSpace(sqlConn),
    cosmos = cosmosDirect is not null,
    api = http.BaseAddress is not null,
    serviceBus = sbClient is not null,
    eventGrid = egClient is not null,
    functionsUrl = Environment.GetEnvironmentVariable("FUNCTIONS_BASEURL") ?? "",
    eventGridEndpoint = egEndpoint,
    memberId = Db.MemberId,
    memberName = Db.MemberName,
}));

// ══════════════════════════════════════════════════════════════════════════
// Exhibit #5 — observability. Drive real traffic through the whole topology so
// the App Insights Application Map draws itself, and emit a couple of custom
// metrics/events on top of the auto-captured telemetry. Nothing here installs an
// agent — instrumentation is the one AddApplicationInsightsTelemetry() call above.
// ══════════════════════════════════════════════════════════════════════════

// Portal deep links to the App Insights blades (App Map, Live Metrics, …). Built
// from AI_RESOURCE_ID, injected post-deploy; null until App Insights is wired.
app.MapGet("/api/observe/links", () =>
{
    var links = PortalLinks.Build(Environment.GetEnvironmentVariable("AI_RESOURCE_ID"));
    return links is null
        ? Results.Json(new { wired = false, note = "App Insights not wired yet — run `make deploy` (it provisions pg-ai and injects the connection string)." })
        : Results.Json(new { wired = true, links });
});

// Generate load: run N end-to-end transactions through web → API → SQL/Cosmos
// (+ the Service Bus / Event Grid fan-out when chain=full), injecting faults into
// a faultPct slice so the map shows failed + slow edges. Each transaction emits a
// custom metric (pg.txn.ms) and event (pg.txn) with outcome/fault dimensions.
app.MapPost("/api/observe/load", async (int? n, int? faultPct, string? chain) =>
{
    var count = Math.Clamp(n ?? 30, 1, 200);
    var pct = Math.Clamp(faultPct ?? 0, 0, 100);
    var full = string.Equals(chain, "full", StringComparison.OrdinalIgnoreCase);
    var id = Db.MemberId;

    var apiOn = http.BaseAddress is not null;
    var cosmosOn = cosmosDirect is not null;
    var fanoutOn = full && (sbFanout is not null || egClient is not null);
    if (!apiOn && !cosmosOn)
        return Results.Json(new { error = "Nothing to exercise — flip enableApi and/or enableCosmos." });

    var ok = 0; var failed = 0; var injected = 0;
    var durations = new List<double>(count);

    for (var i = 0; i < count; i++)
    {
        // Alternate error/slow across actual fault events (not index parity — the
        // fault indices can all share a parity, which would make them all one kind).
        string? faultKind = null;
        if (LoadPlan.IsFaultIndex(i, count, pct))
        {
            faultKind = (injected % 2 == 0) ? "error" : "slow";   // mix red + amber
            injected++;
        }
        var sw = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            if (apiOn)
            {
                // Fault rides on both calls so it fires against whichever backend is
                // wired (SQL off → the Cosmos call carries the red/amber edge).
                var q = faultKind is null ? "" : $"?fault={faultKind}";
                await http.GetFromJsonAsync<ApiResp>($"/ssn/sql/{id}{q}");
                await http.GetFromJsonAsync<ApiResp>($"/ssn/cosmos/{id}{q}");
            }
            if (cosmosOn) await Db.ReadSsnCosmos(cosmosDirect!, id);
            if (fanoutOn)
            {
                var corr = Guid.NewGuid().ToString("N");
                if (sbFanout is not null) await sbFanout.SendMessageAsync(new ServiceBusMessage($"{{\"correlationId\":\"{corr}\"}}"));
                else if (egClient is not null) await egClient.SendEventAsync(new CloudEvent("playground/fanout", "pg.fanout", new { correlationId = corr }));
            }
            ok++;
        }
        catch
        {
            outcome = "failed";   // an injected 500 (or a real error) bubbles to here
            failed++;
        }
        sw.Stop();
        var ms = sw.Elapsed.TotalMilliseconds;
        durations.Add(ms);

        // Custom telemetry layered on top of the auto-captured requests/dependencies.
        telemetry.GetMetric("pg.txn.ms", "outcome").TrackValue(ms, outcome);
        telemetry.TrackEvent("pg.txn", new Dictionary<string, string>
        {
            ["outcome"] = outcome,
            ["fault"] = faultKind ?? "none",
            ["chain"] = full ? "full" : "api",
        });
    }
    telemetry.Flush();   // push before the response so it's queryable promptly

    return Results.Json(new
    {
        n = count,
        ok,
        failed,
        faultsInjected = injected,
        chain = full ? "full" : "api",
        latency = Stats.Of(durations.ToArray()),
        note = "Open the App Map — nodes light up by call volume, the faulted edges go amber/red. Telemetry lands in ~30–90s.",
    });
});

// ══════════════════════════════════════════════════════════════════════════
// Exhibit #4 — fan-out, two ways. Publish one event down a path; each consumer
// writes a receipt to Cosmos, which the page polls for by correlationId.
// ══════════════════════════════════════════════════════════════════════════
app.MapPost("/api/fanout/publish", async (HttpContext ctx) =>
{
    var path = ctx.Request.Query["path"].ToString();          // "sb" | "eg"
    var corr = ctx.Request.Query["cid"].ToString();
    if (string.IsNullOrWhiteSpace(corr)) corr = Guid.NewGuid().ToString("N");
    var payload = $"{{\"correlationId\":\"{corr}\"}}";
    try
    {
        switch (path)
        {
            case "sb":
                if (sbFanout is null) return Results.Json(new { error = "Service Bus not configured (flip enableServiceBus + SB_TOPICS=1)", corr, path });
                await sbFanout.SendMessageAsync(new ServiceBusMessage(payload));
                break;
            case "eg":
                if (egClient is null) return Results.Json(new { error = "Event Grid not configured (flip enableEventGrid + redeploy)", corr, path });
                // Pass the data as a JSON-serializable object so it rides in the CloudEvent's
                // `data` field. (A BinaryData payload lands in `data_base64` instead, which the
                // consumer also handles, but `data` is the clean shape.)
                await egClient.SendEventAsync(new CloudEvent("playground/fanout", "pg.fanout", new { correlationId = corr }));
                break;
            default:
                return Results.Json(new { error = "path must be 'sb' or 'eg'", corr, path });
        }
    }
    catch (Exception ex)
    {
        // On Basic, the 'fanout' topic doesn't exist — surface it instead of 500ing.
        return Results.Json(new { error = ex.Message, corr, path });
    }
    return Results.Json(new { corr, path, ok = true });
});

app.MapGet("/api/fanout/receipts/{cid}", async (string cid) =>
{
    if (fanoutReceipts is null) return Results.Json(new { error = "Cosmos not configured (flip enableCosmos)" });
    var receipts = new List<object>();
    try
    {
        var q = new QueryDefinition("SELECT c.path, c.consumer, c.receivedAt FROM c WHERE c.correlationId = @cid")
            .WithParameter("@cid", cid);
        using var it = fanoutReceipts.GetItemQueryIterator<FanoutReceipt>(q);
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync())
                receipts.Add(new { r.path, r.consumer, r.receivedAt });
    }
    catch (Exception ex)
    {
        return Results.Json(new { cid, receipts, note = ex.Message });
    }
    return Results.Json(new { cid, receipts });
});

// ══════════════════════════════════════════════════════════════════════════
// Exhibit #2 — Service Bus for synchronous reads (a cautionary tale).
// Turn one DB read into a request/reply round-trip through a broker. The
// optional ?poll= adds a naive client poll delay to show how much worse a
// hand-rolled receive loop makes it.
// ══════════════════════════════════════════════════════════════════════════
app.MapGet("/api/ssn/via-servicebus/{id}", async (string id, int? poll) =>
{
    if (sbClient is null || sbRequests is null)
        return Results.Json(new { error = "Service Bus not configured (flip enableServiceBus)" });

    var corr = Guid.NewGuid().ToString("N");
    var sw = Stopwatch.StartNew();
    await sbRequests.SendMessageAsync(new ServiceBusMessage(id) { CorrelationId = corr });

    await using var rx = sbClient.CreateReceiver("reveal-replies");
    string? ssn = null;
    var got = false;
    // Single-user exhibit: take the next reply rather than strict-correlate +
    // abandon (which, with no sessions on Basic, causes redelivery churn and the
    // occasional lost reply). Still measures the full broker round-trip.
    if (poll is > 0) await Task.Delay(poll.Value);
    var msg = await rx.ReceiveMessageAsync(TimeSpan.FromSeconds(15));
    if (msg is not null)
    {
        ssn = msg.Body.ToString();
        await rx.CompleteMessageAsync(msg);
        got = true;
    }
    sw.Stop();
    return Results.Json(new
    {
        ssn = string.IsNullOrEmpty(ssn) ? null : ssn,
        path = "via-servicebus",
        serverMs = sw.Elapsed.TotalMilliseconds,
        pollMs = poll ?? 0,
        timedOut = !got,
    });
});

// ══════════════════════════════════════════════════════════════════════════
// Exhibit #1 — SSN reveal latency: direct SQL vs API→SQL vs API→Cosmos
// ══════════════════════════════════════════════════════════════════════════

app.MapGet("/api/ssn/direct/{id}", async (string id) =>
{
    if (string.IsNullOrWhiteSpace(sqlConn)) return Results.Json(new { error = "SQL not configured (flip enableSql)" });
    var sw = Stopwatch.StartNew();
    var ssn = await Db.ReadSsnSql(sqlConn, id);
    sw.Stop();
    return Results.Json(new { ssn, path = "direct", serverMs = sw.Elapsed.TotalMilliseconds });
});

app.MapGet("/api/ssn/direct-cosmos/{id}", async (string id) =>
{
    if (cosmosDirect is null) return Results.Json(new { error = "Cosmos not configured (flip enableCosmos)" });
    var sw = Stopwatch.StartNew();
    var ssn = await Db.ReadSsnCosmos(cosmosDirect, id);
    sw.Stop();
    return Results.Json(new { ssn, path = "direct-cosmos", serverMs = sw.Elapsed.TotalMilliseconds });
});

app.MapGet("/api/ssn/via-api-sql/{id}", async (string id) =>
{
    if (http.BaseAddress is null) return Results.Json(new { error = "API not configured (flip enableApi)" });
    var sw = Stopwatch.StartNew();
    var r = await http.GetFromJsonAsync<ApiResp>($"/ssn/sql/{id}");
    sw.Stop();
    return Results.Json(new { ssn = r?.ssn, path = "via-api-sql", serverMs = sw.Elapsed.TotalMilliseconds, apiDbMs = r?.dbMs });
});

app.MapGet("/api/ssn/via-api-cosmos/{id}", async (string id) =>
{
    if (http.BaseAddress is null) return Results.Json(new { error = "API not configured (flip enableApi)" });
    var sw = Stopwatch.StartNew();
    var r = await http.GetFromJsonAsync<ApiResp>($"/ssn/cosmos/{id}");
    sw.Stop();
    return Results.Json(new { ssn = r?.ssn, path = "via-api-cosmos", serverMs = sw.Elapsed.TotalMilliseconds, apiDbMs = r?.dbMs });
});

// Server-side benchmark — N samples/path, one warmup excluded per path so the
// headline number is steady-state (the cold-start caveat is a separate story).
app.MapGet("/api/bench", async (int n, string? id) =>
{
    n = Math.Clamp(n <= 0 ? 50 : n, 1, 500);
    id ??= Db.MemberId;

    async Task<double[]> Measure(bool enabled, Func<Task> action)
    {
        if (!enabled) return Array.Empty<double>();
        for (var w = 0; w < 5; w++) await action(); // warm the connection pool, excluded
        var xs = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            xs[i] = sw.Elapsed.TotalMilliseconds;
        }
        return xs;
    }

    var sqlOn = !string.IsNullOrWhiteSpace(sqlConn);
    var cosmosOn = cosmosDirect is not null;
    var apiOn = http.BaseAddress is not null;

    var direct = await Measure(sqlOn, async () => await Db.ReadSsnSql(sqlConn, id));
    var directCosmos = await Measure(cosmosOn, async () => await Db.ReadSsnCosmos(cosmosDirect!, id));
    var apiSql = await Measure(apiOn, async () => await http.GetFromJsonAsync<ApiResp>($"/ssn/sql/{id}"));
    var apiCosmos = await Measure(apiOn, async () => await http.GetFromJsonAsync<ApiResp>($"/ssn/cosmos/{id}"));

    return Results.Json(new
    {
        n,
        direct = Stats.Of(direct),
        directCosmos = Stats.Of(directCosmos),
        apiSql = Stats.Of(apiSql),
        apiCosmos = Stats.Of(apiCosmos),
    });
});

app.Run();

record ApiResp(string? ssn, string source, double dbMs, string? error);

// Exhibit #4 receipt projection (lowercase to match the Cosmos doc fields).
record FanoutReceipt(string path, string consumer, string receivedAt);
