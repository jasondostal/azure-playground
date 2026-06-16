using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Playground.Functions;

// Scenario 4 — the adapter: queue → serverless function → push to another
// system. The MeridianLink→DNA shape. Non-2xx throws, so the message is
// abandoned and retried, then dead-lettered — retry semantics made visible.
public class Adapter
{
    private readonly HttpClient _http;
    private readonly ILogger<Adapter> _log;
    public Adapter(IHttpClientFactory f, ILogger<Adapter> log) { _http = f.CreateClient(); _log = log; }

    [Function("QueueToTarget")]
    public async Task QueueToTarget(
        [ServiceBusTrigger("to-target", Connection = "ServiceBusConnection")] string message)
    {
        var baseUrl = (Environment.GetEnvironmentVariable("TARGET_API_BASEURL") ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) { _log.LogWarning("TARGET_API_BASEURL unset; dropping"); return; }

        var resp = await _http.PostAsync($"{baseUrl}/healthz",
            new StringContent(message, System.Text.Encoding.UTF8, "application/json"));
        _log.LogInformation("Forwarded to target → {Status}", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"target returned {(int)resp.StatusCode} — abandon & retry"); // → dead-letter after max
    }
}
