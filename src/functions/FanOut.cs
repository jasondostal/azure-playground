using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Playground.Functions;

// Scenario 3 — publish once to a TOPIC, two independent subscribers each get a
// copy (fan-out). Distinct from a queue, where competing consumers split the
// work and a message is handled once. Topic/subs need Service Bus Standard
// (SB_TOPICS=1); ConsumerA/B are disabled via app settings on Basic.
public class FanOut
{
    private readonly ILogger<FanOut> _log;
    public FanOut(ILogger<FanOut> log) => _log = log;

    public class PublishOutput
    {
        [ServiceBusOutput("events", Connection = "ServiceBusConnection")]
        public string? Event { get; set; }
        public HttpResponseData? HttpResponse { get; set; }
    }

    [Function("Publish")]
    public async Task<PublishOutput> Publish(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "publish")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var resp = req.CreateResponse(HttpStatusCode.Accepted);
        await resp.WriteStringAsync("published to topic 'events'");
        return new PublishOutput { Event = string.IsNullOrWhiteSpace(body) ? "{}" : body, HttpResponse = resp };
    }

    [Function("ConsumerA")]
    public void ConsumerA([ServiceBusTrigger("events", "sub-a", Connection = "ServiceBusConnection")] string msg)
        => _log.LogInformation("ConsumerA (sub-a) got its copy: {Msg}", msg);

    [Function("ConsumerB")]
    public void ConsumerB([ServiceBusTrigger("events", "sub-b", Connection = "ServiceBusConnection")] string msg)
        => _log.LogInformation("ConsumerB (sub-b) got its copy: {Msg}", msg);
}
