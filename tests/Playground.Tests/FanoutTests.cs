using System.Text;
using Playground.Functions;
using Xunit;

namespace Playground.Tests;

// Exhibit #4 — the receipt sink is only useful if every consumer pulls the SAME
// correlationId out of a message, regardless of the shape it arrives in. The
// Service Bus body, the Event Grid CloudEvent, and a base64-encoded queue
// delivery all look different. These pin that down so a publish actually
// correlates to its receipts (and the UI lights up) instead of failing quietly.
public class FanoutTests
{
    [Fact]
    public void ServiceBusBody_PlainCorrelationId()
        => Assert.Equal("abc123", Fanout4.ExtractCorrelationId("{\"correlationId\":\"abc123\"}"));

    [Fact]
    public void CloudEvent_NestedUnderData()
        => Assert.Equal("xyz", Fanout4.ExtractCorrelationId(
            "{\"type\":\"pg.fanout\",\"data\":{\"correlationId\":\"xyz\"},\"specversion\":\"1.0\"}"));

    [Fact]
    public void Base64EncodedCloudEvent_IsDecoded()
    {
        var json = "{\"type\":\"pg.fanout\",\"data\":{\"correlationId\":\"b64id\"}}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Equal("b64id", Fanout4.ExtractCorrelationId(b64));
    }

    [Fact]
    public void CloudEvent_BinaryDataUnderDataBase64()
    {
        // How Event Grid actually delivers a BinaryData payload: the inner JSON is
        // base64'd under `data_base64` (the bug that ate the first live run).
        var inner = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"correlationId\":\"deep\"}"));
        var ce = $"{{\"type\":\"pg.fanout\",\"data_base64\":\"{inner}\",\"specversion\":\"1.0\"}}";
        Assert.Equal("deep", Fanout4.ExtractCorrelationId(ce));
    }

    [Fact]
    public void NonJson_FallsBackToTrimmedRaw()
        => Assert.Equal("not-json", Fanout4.ExtractCorrelationId("  not-json  "));

    [Fact]
    public void Empty_ReturnsEmpty()
        => Assert.Equal("", Fanout4.ExtractCorrelationId("   "));

    [Theory]
    [InlineData("servicebus", "A")]
    [InlineData("eventgrid", "B")]
    public void Receipt_From_CapturesPathAndConsumer(string path, string consumer)
    {
        var r = Receipt.From("{\"correlationId\":\"c1\"}", path, consumer);
        Assert.Equal("c1", r.correlationId);
        Assert.Equal(path, r.path);
        Assert.Equal(consumer, r.consumer);
        Assert.False(string.IsNullOrWhiteSpace(r.id));
        Assert.False(string.IsNullOrWhiteSpace(r.receivedAt));
    }
}
