using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Playground.Functions;

// Scenario 2 — synchronous APIs hosted on Functions, talking to Cosmos via
// input/output bindings (no hand-written SDK calls). The POST also seeds
// scenario 5 (change feed).
public class Items
{
    [Function("ApiGet")]
    public async Task<HttpResponseData> ApiGet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "items/{id}")] HttpRequestData req,
        [CosmosDBInput("playground", "items", Connection = "CosmosDbConnection", Id = "{id}", PartitionKey = "{id}")] Item? item)
    {
        if (item is null) return req.CreateResponse(HttpStatusCode.NotFound);
        var r = req.CreateResponse(HttpStatusCode.OK);
        await r.WriteAsJsonAsync(item);
        return r;
    }

    public class CreateItemOutput
    {
        [CosmosDBOutput("playground", "items", Connection = "CosmosDbConnection", CreateIfNotExists = true)]
        public Item? Doc { get; set; }
        public HttpResponseData? HttpResponse { get; set; }
    }

    [Function("ApiPost")]
    public async Task<CreateItemOutput> ApiPost(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "items")] HttpRequestData req)
    {
        var item = await req.ReadFromJsonAsync<Item>();
        if (item is null || string.IsNullOrWhiteSpace(item.id))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("body must include an id");
            return new CreateItemOutput { Doc = null, HttpResponse = bad };
        }
        var ok = req.CreateResponse(HttpStatusCode.Created);
        await ok.WriteAsJsonAsync(item);
        return new CreateItemOutput { Doc = item, HttpResponse = ok };
    }
}
