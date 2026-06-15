using System.Diagnostics;
using System.Net.Http.Json;
using Playground.App;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // wwwroot lives next to the binary
});
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Config (env-driven; empty = "not bolted on yet") ──────────────────────
var sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION") ?? "";
var apiBase = Environment.GetEnvironmentVariable("API_BASE") ?? "";

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
if (!string.IsNullOrWhiteSpace(apiBase)) http.BaseAddress = new Uri(apiBase);

await Db.EnsureSqlSeed(sqlConn, app.Logger);

app.MapGet("/healthz", () => Results.Text("ok"));

// What's wired right now — drives the exhibit's "flip enableX" hints.
app.MapGet("/api/config", () => Results.Json(new
{
    sql = !string.IsNullOrWhiteSpace(sqlConn),
    api = http.BaseAddress is not null,
    memberId = Db.MemberId,
    memberName = Db.MemberName,
}));

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
        await action(); // warmup, excluded
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
    var apiOn = http.BaseAddress is not null;

    var direct = await Measure(sqlOn, async () => await Db.ReadSsnSql(sqlConn, id));
    var apiSql = await Measure(apiOn, async () => await http.GetFromJsonAsync<ApiResp>($"/ssn/sql/{id}"));
    var apiCosmos = await Measure(apiOn, async () => await http.GetFromJsonAsync<ApiResp>($"/ssn/cosmos/{id}"));

    return Results.Json(new
    {
        n,
        direct = Stats.Of(direct),
        apiSql = Stats.Of(apiSql),
        apiCosmos = Stats.Of(apiCosmos),
    });
});

app.Run();

record ApiResp(string? ssn, string source, double dbMs, string? error);
