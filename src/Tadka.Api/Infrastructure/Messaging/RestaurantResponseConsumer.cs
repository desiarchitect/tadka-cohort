using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Tadka.Telemetry;

namespace Tadka.Api.Infrastructure.Messaging;

/// <summary>
/// Production multi-service path (ADR-062): Restaurant.Api publishes <c>restaurant-response</c>.
/// Delegates business work to <see cref="RestaurantResponseHandler"/> (testable without Kafka).
/// </summary>
public sealed class RestaurantResponseConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<RestaurantResponseConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.RestaurantResponse);
        logger.LogInformation("RestaurantResponseConsumer subscribed to {Topic}.", Topics.RestaurantResponse);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is null) continue;

                using var activity = TadkaDiagnostics.ActivitySource.StartActivity(
                    $"consume {Topics.RestaurantResponse}", ActivityKind.Consumer, TadkaTrace.ParseContext(ReadTraceParent(cr)));

                var msg = JsonSerializer.Deserialize<RestaurantResponseMessage>(cr.Message.Value);
                if (msg is not null)
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RestaurantResponseHandler>()
                        .HandleAsync(msg, stoppingToken);
                }

                consumer.Commit(cr);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex) { logger.LogError(ex, "RestaurantResponseConsumer consume error."); }
            catch (Exception ex) { logger.LogError(ex, "RestaurantResponseConsumer handler error."); }
        }

        consumer.Close();
    }, stoppingToken);

    private static string? ReadTraceParent(ConsumeResult<string, string> cr) =>
        cr.Message.Headers is not null && cr.Message.Headers.TryGetLastBytes(TadkaTrace.TraceParentHeader, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;
}
