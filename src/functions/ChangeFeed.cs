using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Playground.Functions;

// Scenario 5 — watch a database for new records → publish an event. Cosmos
// change feed is the cleanest, real-time source (native, no polling). New docs
// in `items` (written by ApiPost) emit a message to `db-events`.
public class ChangeFeed
{
    private readonly ILogger<ChangeFeed> _log;
    public ChangeFeed(ILogger<ChangeFeed> log) => _log = log;

    [Function("CosmosChangeFeed")]
    [ServiceBusOutput("db-events", Connection = "ServiceBusConnection")]
    public string[] Run(
        [CosmosDBTrigger(
            databaseName: "playground",
            containerName: "items",
            Connection = "CosmosDbConnection",
            LeaseContainerName = "leases",
            CreateLeaseContainerIfNotExists = true)] IReadOnlyList<Item> changes)
    {
        _log.LogInformation("Change feed: {Count} new/updated doc(s)", changes.Count);
        return changes.Select(c => $"{{\"event\":\"item-changed\",\"id\":\"{c.id}\"}}").ToArray();
    }

    // SqlPollAlt (reference, disabled) — the SQL-source variant: a timer that
    // polls a table by watermark and publishes. Cosmos change feed is preferred;
    // this exists to show the shape when the source is SQL. Left commented so it
    // doesn't run by default.
    //
    // [Function("SqlPollAlt")]
    // [ServiceBusOutput("db-events", Connection = "ServiceBusConnection")]
    // public string[] SqlPollAlt([TimerTrigger("0 */1 * * * *")] TimerInfo t)
    // {
    //     // SELECT ... WHERE RowVersion > @watermark; persist new watermark; emit events
    //     return Array.Empty<string>();
    // }
}
