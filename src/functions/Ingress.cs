using System.Net;
using Azure.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Playground.Functions;

// Scenario 1 — webhook / event ingress, two paths into the SAME queue so
// everything downstream is path-agnostic.
public class Ingress
{
    private readonly ILogger<Ingress> _log;
    public Ingress(ILogger<Ingress> log) => _log = log;

    // Raw, simplest path: HTTP POST → validate → queue `ingress`.
    public class WebhookOutput
    {
        [ServiceBusOutput("ingress", Connection = "ServiceBusConnection")]
        public string? Message { get; set; } // null = nothing enqueued
        public HttpResponseData? HttpResponse { get; set; }
    }

    [Function("WebhookDirect")]
    public async Task<WebhookOutput> WebhookDirect(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var ok = !string.IsNullOrWhiteSpace(body);
        var resp = req.CreateResponse(ok ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        await resp.WriteStringAsync(ok ? "queued" : "empty body");
        return new WebhookOutput { Message = ok ? body : null, HttpResponse = resp };
    }

    // Broker-fronted path: Event Grid custom topic → function → same `ingress`
    // queue. Event Grid brings retries / dead-letter / fan-out for free.
    [Function("EventGridIngress")]
    [ServiceBusOutput("ingress", Connection = "ServiceBusConnection")]
    public string EventGridIngress([EventGridTrigger] CloudEvent ce)
    {
        _log.LogInformation("EventGrid event {Type} from {Source}", ce.Type, ce.Source);
        return ce.Data?.ToString() ?? "{}";
    }
}
