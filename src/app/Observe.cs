namespace Playground.App;

// Exhibit #5 — observability helpers (pure, unit-tested).

// Build Azure Portal deep links to the App Insights blades from the component's
// ARM resource id (injected post-deploy as AI_RESOURCE_ID). The leading "#@/"
// resolves to the signed-in tenant, so these open straight to the resource for
// whoever's already in the portal.
public static class PortalLinks
{
    public static Dictionary<string, string>? Build(string? aiResourceId)
    {
        if (string.IsNullOrWhiteSpace(aiResourceId)) return null;
        var id = aiResourceId.TrimEnd('/');
        string Blade(string b) => $"https://portal.azure.com/#@/resource{id}/{b}";
        return new()
        {
            // The headline: the auto-discovered topology with call rates + failures.
            ["appMap"]      = Blade("applicationMap"),
            // Real-time, 1s-resolution stream — no sampling, no ingestion delay.
            ["liveMetrics"] = Blade("quickPulse"),
            ["failures"]    = Blade("failures"),
            ["performance"] = Blade("performance"),
            // Raw KQL surface (the query pack in docs/observability-kql.md).
            ["logs"]        = Blade("logs"),
        };
    }
}

// Spreads a fault percentage evenly across N transactions so the App Map shows a
// steady amber/red fraction rather than a clump. Pure + deterministic so the load
// generator and its tests agree on exactly how many faults a run will inject.
public static class LoadPlan
{
    // Exact number of faulted transactions for n iterations at pct% (floored).
    public static int FaultCount(int n, int pct)
    {
        if (n <= 0 || pct <= 0) return 0;
        if (pct >= 100) return n;
        return (int)((long)n * pct / 100);
    }

    // True when transaction i (0-based) should carry a fault. Evenly distributed:
    // the count of true results over 0..n-1 equals FaultCount(n, pct).
    public static bool IsFaultIndex(int i, int n, int pct)
    {
        if (n <= 0 || pct <= 0 || i < 0 || i >= n) return false;
        if (pct >= 100) return true;
        return FaultCount(i + 1, pct) > FaultCount(i, pct);
    }
}
