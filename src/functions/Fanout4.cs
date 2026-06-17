using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Playground.Functions;

// Exhibit #4 — fan-out, two ways. One published event reaches two independent
// consumers, each getting its own copy. This file holds only the CONSUMERS; the
// app (src/app) does the publishing so the UI drives both paths symmetrically.
//
//   Path A (Service Bus):   topic 'fanout' → subscriptions f-sub-a / f-sub-b
//                           (needs Service Bus Standard / SB_TOPICS=1)
//   Path B (Event Grid):    Event Grid topic → two subscriptions → two Storage
//                           Queues (fanout-a / fanout-b) → these queue triggers
//
// Every consumer writes a small receipt to the Cosmos 'fanout' container. The
// app reads receipts back by correlationId, so the page can show each consumer
// light up independently. (Distinct from #3's scenario-3 fan-out, which is left
// untouched — these are parallel functions with their own topic/subs.)
public class Fanout4
{
    private readonly ILogger<Fanout4> _log;
    public Fanout4(ILogger<Fanout4> log) => _log = log;

    // ── Service Bus path: one subscriber per topic subscription ──────────────
    [Function("Fan4SbA")]
    [CosmosDBOutput("playground", "fanout", Connection = "CosmosDbConnection", CreateIfNotExists = true)]
    public Receipt Fan4SbA(
        [ServiceBusTrigger("fanout", "f-sub-a", Connection = "ServiceBusConnection")] string msg)
    {
        _log.LogInformation("Fan4 ServiceBus · A got its copy: {Msg}", msg);
        return Receipt.From(msg, "servicebus", "A");
    }

    [Function("Fan4SbB")]
    [CosmosDBOutput("playground", "fanout", Connection = "CosmosDbConnection", CreateIfNotExists = true)]
    public Receipt Fan4SbB(
        [ServiceBusTrigger("fanout", "f-sub-b", Connection = "ServiceBusConnection")] string msg)
    {
        _log.LogInformation("Fan4 ServiceBus · B got its copy: {Msg}", msg);
        return Receipt.From(msg, "servicebus", "B");
    }

    // ── Event Grid path: one subscriber per Storage Queue the topic fans to ──
    [Function("Fan4EgA")]
    [CosmosDBOutput("playground", "fanout", Connection = "CosmosDbConnection", CreateIfNotExists = true)]
    public Receipt Fan4EgA(
        [QueueTrigger("fanout-a", Connection = "FanoutStorageConnection")] string msg)
    {
        _log.LogInformation("Fan4 EventGrid · A got its copy: {Msg}", msg);
        return Receipt.From(msg, "eventgrid", "A");
    }

    [Function("Fan4EgB")]
    [CosmosDBOutput("playground", "fanout", Connection = "CosmosDbConnection", CreateIfNotExists = true)]
    public Receipt Fan4EgB(
        [QueueTrigger("fanout-b", Connection = "FanoutStorageConnection")] string msg)
    {
        _log.LogInformation("Fan4 EventGrid · B got its copy: {Msg}", msg);
        return Receipt.From(msg, "eventgrid", "B");
    }

    // Pulls the correlationId out of whatever shape the message arrives in:
    //   • Service Bus body:   {"correlationId":"…"}
    //   • Event Grid event:   {"type":"pg.fanout","data":{"correlationId":"…"},…}
    //   • Storage-queue delivery may base64-encode the event, so try that too.
    // Falls back to the raw body so a receipt is still recorded (just uncorrelated).
    public static string ExtractCorrelationId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var candidates = new List<string> { raw };
        try { candidates.Add(Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim()))); } catch { /* not base64 */ }

        foreach (var s in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                // CloudEvent with a JSON data member: { "data": { "correlationId": … } }
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("correlationId", out var c1) && c1.ValueKind == JsonValueKind.String)
                    return c1.GetString()!;
                // CloudEvent with a binary data member: { "data_base64": "<base64 of {correlationId}>" }
                if (root.TryGetProperty("data_base64", out var db) && db.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        var inner = Encoding.UTF8.GetString(Convert.FromBase64String(db.GetString()!));
                        using var idoc = JsonDocument.Parse(inner);
                        if (idoc.RootElement.ValueKind == JsonValueKind.Object
                            && idoc.RootElement.TryGetProperty("correlationId", out var c3) && c3.ValueKind == JsonValueKind.String)
                            return c3.GetString()!;
                    }
                    catch { /* not base64-wrapped JSON */ }
                }
                // Bare body: { "correlationId": … }
                if (root.TryGetProperty("correlationId", out var c2) && c2.ValueKind == JsonValueKind.String)
                    return c2.GetString()!;
            }
            catch { /* not JSON in this candidate */ }
        }
        return raw.Trim();
    }
}

// A receipt: which consumer, on which path, received the event — keyed by the
// correlationId the publisher stamped on it. Written to Cosmos 'fanout'.
public sealed class Receipt
{
    public string id { get; set; } = Guid.NewGuid().ToString("N");
    public string correlationId { get; set; } = "";
    public string path { get; set; } = "";       // "servicebus" | "eventgrid"
    public string consumer { get; set; } = "";   // "A" | "B"
    public string receivedAt { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    public static Receipt From(string raw, string path, string consumer) => new()
    {
        correlationId = Fanout4.ExtractCorrelationId(raw),
        path = path,
        consumer = consumer,
    };
}
