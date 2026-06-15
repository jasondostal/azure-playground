namespace Playground.App;

// Tiny latency-distribution summariser for the benchmark endpoint.
public static class Stats
{
    public static object? Of(double[] xs)
    {
        if (xs.Length == 0) return null;
        var s = (double[])xs.Clone();
        Array.Sort(s);
        var sum = 0.0;
        foreach (var x in s) sum += x;
        double P(double q) => s[Math.Clamp((int)Math.Ceiling(q * s.Length) - 1, 0, s.Length - 1)];
        return new
        {
            count = s.Length,
            min = s[0],
            avg = sum / s.Length,
            p50 = P(0.50),
            p95 = P(0.95),
            max = s[^1],
        };
    }
}
