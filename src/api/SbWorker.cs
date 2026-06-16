using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Playground.Api;

// Exhibit #2 — the cautionary tale. This turns an async message broker into a
// (deliberately ill-advised) synchronous request/reply RPC: it consumes SSN
// "requests" off a queue, reads SQL, and pushes the answer onto a reply queue.
// It exists to MEASURE how bad using a queue for synchronous reads gets.
public sealed class SbWorker : BackgroundService
{
    public const string RequestQueue = "reveal-requests";
    public const string ReplyQueue = "reveal-replies";

    private readonly string _sbConn = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION") ?? "";
    private readonly string _sqlConn = Environment.GetEnvironmentVariable("SQL_CONNECTION") ?? "";
    private readonly ILogger<SbWorker> _log;

    public SbWorker(ILogger<SbWorker> log) => _log = log;

    public static ServiceBusClientOptions ClientOptions => new()
    {
        // WebSockets (443) so the App Service sandbox can't block AMQP's 5671.
        TransportType = ServiceBusTransportType.AmqpWebSockets,
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sbConn)) { _log.LogInformation("Service Bus not configured; worker idle"); return; }
        try
        {
            var admin = new ServiceBusAdministrationClient(_sbConn);
            foreach (var q in new[] { RequestQueue, ReplyQueue })
                if (!await admin.QueueExistsAsync(q, ct))
                    await admin.CreateQueueAsync(q, ct);

            var client = new ServiceBusClient(_sbConn, ClientOptions);
            var replySender = client.CreateSender(ReplyQueue);
            var processor = client.CreateProcessor(RequestQueue,
                new ServiceBusProcessorOptions { MaxConcurrentCalls = 1, AutoCompleteMessages = false });

            processor.ProcessMessageAsync += async args =>
            {
                var id = args.Message.Body.ToString();
                string? ssn = null;
                try { ssn = await Db.ReadSsnSql(_sqlConn, id); }
                catch (Exception ex) { _log.LogWarning(ex, "SB worker SQL read failed"); }
                var reply = new ServiceBusMessage(ssn ?? "") { CorrelationId = args.Message.CorrelationId };
                await replySender.SendMessageAsync(reply, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message);
            };
            processor.ProcessErrorAsync += _ => Task.CompletedTask;

            await processor.StartProcessingAsync(ct);
            _log.LogInformation("SB worker consuming {Queue}", RequestQueue);
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _log.LogError(ex, "SB worker crashed"); }
    }
}
