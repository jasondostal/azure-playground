using Playground.Api;
using Playground.App;
using Xunit;

namespace Playground.Tests;

// Exhibit #5 — the load generator and fault injector only tell an honest story on
// the App Map if the pure bits underneath behave: faults parse the way callers
// expect, the fault percentage spreads to an exact, even count, and the portal
// links point at the right blades. These pin that down without touching Azure.
public class ObservabilityTests
{
    // ── Fault parsing (API tier) ──────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void Fault_BlankOrUnknown_IsNone(string? raw)
        => Assert.Equal(FaultMode.None, FaultSpec.Parse(raw).Mode);

    [Theory]
    [InlineData("error")]
    [InlineData("ERROR")]
    [InlineData("fail")]
    [InlineData("500")]
    public void Fault_Error_Variants(string raw)
        => Assert.Equal(FaultMode.Error, FaultSpec.Parse(raw).Mode);

    [Fact]
    public void Fault_Slow_DefaultDelay()
    {
        var f = FaultSpec.Parse("slow");
        Assert.Equal(FaultMode.Slow, f.Mode);
        Assert.Equal(FaultSpec.DefaultSlowMs, f.DelayMs);
    }

    [Fact]
    public void Fault_Slow_ExplicitDelay()
        => Assert.Equal(1200, FaultSpec.Parse("slow:1200").DelayMs);

    [Fact]
    public void Fault_Slow_BadDelay_FallsBackToDefault()
        => Assert.Equal(FaultSpec.DefaultSlowMs, FaultSpec.Parse("slow:abc").DelayMs);

    [Fact]
    public void Fault_Slow_DelayIsClamped()
        => Assert.Equal(30_000, FaultSpec.Parse("slow:999999").DelayMs);

    [Fact]
    public async Task Fault_Error_ApplyThrows()
        => await Assert.ThrowsAsync<InjectedFaultException>(() => FaultSpec.Parse("error").ApplyAsync());

    [Fact]
    public async Task Fault_None_ApplyIsNoOp()
        => await FaultSpec.None.ApplyAsync();   // completes without throwing

    // ── Fault distribution (load generator) ───────────────────────────────
    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(10, 30, 3)]
    [InlineData(10, 100, 10)]
    [InlineData(50, 20, 10)]
    [InlineData(7, 50, 3)]    // floor(3.5)
    public void FaultCount_IsFloored(int n, int pct, int expected)
        => Assert.Equal(expected, LoadPlan.FaultCount(n, pct));

    [Theory]
    [InlineData(100, 0)]
    [InlineData(100, 17)]
    [InlineData(100, 50)]
    [InlineData(100, 100)]
    [InlineData(33, 30)]
    public void IsFaultIndex_CountMatchesFaultCount(int n, int pct)
    {
        var hits = 0;
        for (var i = 0; i < n; i++) if (LoadPlan.IsFaultIndex(i, n, pct)) hits++;
        Assert.Equal(LoadPlan.FaultCount(n, pct), hits);
    }

    [Fact]
    public void IsFaultIndex_OutOfRange_IsFalse()
    {
        Assert.False(LoadPlan.IsFaultIndex(-1, 10, 50));
        Assert.False(LoadPlan.IsFaultIndex(10, 10, 50));
    }

    [Fact]
    public void IsFaultIndex_SpreadsEvenly_NotClumped()
    {
        // 50% over 10 should alternate, not bunch the first five together.
        var pattern = new bool[10];
        for (var i = 0; i < 10; i++) pattern[i] = LoadPlan.IsFaultIndex(i, 10, 50);
        // No three consecutive identical results — i.e. it's interleaved.
        for (var i = 0; i + 2 < 10; i++)
            Assert.False(pattern[i] == pattern[i + 1] && pattern[i + 1] == pattern[i + 2],
                $"three in a row at {i}");
    }

    // ── Portal links ──────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PortalLinks_Unwired_IsNull(string? id)
        => Assert.Null(PortalLinks.Build(id));

    [Fact]
    public void PortalLinks_BuildsAllBlades()
    {
        const string id = "/subscriptions/0000/resourceGroups/rg-pg-playground/providers/microsoft.insights/components/pg-ai";
        var links = PortalLinks.Build(id);
        Assert.NotNull(links);
        foreach (var blade in new[] { "appMap", "liveMetrics", "failures", "performance", "logs" })
            Assert.True(links!.ContainsKey(blade), $"missing {blade}");
        Assert.EndsWith("/applicationMap", links!["appMap"]);
        Assert.Contains(id, links["appMap"]);
        Assert.StartsWith("https://portal.azure.com/", links["appMap"]);
    }
}
