using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Playground.Api;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Config (env-driven; empty = that backend is "not bolted on yet") ──────
var sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION") ?? "";
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "";
var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "";
const string CosmosDb = "playground";
const string CosmosContainer = "members";

// Cosmos via account key (App Service Free has no managed identity). Gateway
// mode = HTTPS/443 only, which the App Service sandbox allows (Direct needs a
// TCP port range the sandbox blocks).
Container? cosmos = null;
if (!string.IsNullOrWhiteSpace(cosmosEndpoint) && !string.IsNullOrWhiteSpace(cosmosKey))
{
    var client = new CosmosClient(cosmosEndpoint, cosmosKey,
        new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
    cosmos = client.GetContainer(CosmosDb, CosmosContainer);
}

// Idempotent seed — best-effort, never crash the limb if a backend is warming.
await Db.EnsureSqlSeed(sqlConn, app.Logger);

// Cosmos seed runs in the BACKGROUND with retry: the data-plane role assignment
// (and the account itself) land after this container starts, so blocking here
// would fail. Reads tolerate a missing item, so the benchmark works regardless.
if (cosmos is not null)
{
    _ = Task.Run(async () =>
    {
        for (var attempt = 1; attempt <= 18; attempt++)
        {
            try { await Db.EnsureCosmosSeed(cosmos); app.Logger.LogInformation("Cosmos seeded"); return; }
            catch (Exception ex)
            {
                app.Logger.LogWarning("Cosmos seed attempt {Attempt} failed: {Msg}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    });
}

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/ssn/sql/{id}", async (string id) =>
{
    if (string.IsNullOrWhiteSpace(sqlConn))
        return Results.Json(new { ssn = (string?)null, source = "sql-via-api", dbMs = 0.0, error = "SQL not configured (flip enableSql)" });
    var sw = Stopwatch.StartNew();
    var ssn = await Db.ReadSsnSql(sqlConn, id);
    sw.Stop();
    return Results.Json(new { ssn, source = "sql-via-api", dbMs = sw.Elapsed.TotalMilliseconds });
});

app.MapGet("/ssn/cosmos/{id}", async (string id) =>
{
    if (cosmos is null)
        return Results.Json(new { ssn = (string?)null, source = "cosmos-via-api", dbMs = 0.0, error = "Cosmos not configured (flip enableCosmos)" });
    var sw = Stopwatch.StartNew();
    var ssn = await Db.ReadSsnCosmos(cosmos, id);
    sw.Stop();
    return Results.Json(new { ssn, source = "cosmos-via-api", dbMs = sw.Elapsed.TotalMilliseconds });
});

app.Run();
