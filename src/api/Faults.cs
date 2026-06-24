namespace Playground.Api;

// Exhibit #5 — opt-in fault injection. The observability exhibit drives load and
// asks a fraction of calls to misbehave so the App Map paints amber/red edges and
// the Failures/Performance blades have something real to show. Off by default, so
// the latency exhibit (#1) and everyone else stay clean — a fault only happens
// when a caller explicitly passes ?fault=.
//
//   ?fault=error      → throw → the dependency edge goes red (5xx)
//   ?fault=slow       → inject 400ms → a slow dependency on the map
//   ?fault=slow:1200  → inject 1200ms
public enum FaultMode { None, Error, Slow }

public readonly record struct FaultSpec(FaultMode Mode, int DelayMs)
{
    public const int DefaultSlowMs = 400;
    public static readonly FaultSpec None = new(FaultMode.None, 0);

    // Pure + total: never throws, unknown input is treated as no fault.
    public static FaultSpec Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return None;
        var s = raw.Trim().ToLowerInvariant();
        if (s == "error" || s == "fail" || s == "500") return new(FaultMode.Error, 0);
        if (s == "slow") return new(FaultMode.Slow, DefaultSlowMs);
        if (s.StartsWith("slow:"))
        {
            var ms = int.TryParse(s.AsSpan(5), out var v) ? Math.Clamp(v, 1, 30_000) : DefaultSlowMs;
            return new(FaultMode.Slow, ms);
        }
        return None;
    }

    // Apply the fault: a slow fault delays the response; an error fault throws
    // (the endpoint lets it bubble to a 500 so it lands on the map as a failure).
    public async Task ApplyAsync()
    {
        switch (Mode)
        {
            case FaultMode.Slow:
                await Task.Delay(DelayMs);
                break;
            case FaultMode.Error:
                throw new InjectedFaultException();
        }
    }
}

public sealed class InjectedFaultException()
    : Exception("Injected fault (Exhibit #5) — this 500 is intentional, to light up the App Map.");
