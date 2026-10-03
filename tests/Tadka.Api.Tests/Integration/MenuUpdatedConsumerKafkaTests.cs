using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Api.Data;
using Tadka.Api.Data.ReadModel;
using Tadka.Api.Infrastructure.Messaging;

namespace Tadka.Api.Tests.Integration;

/// <summary>
/// Real-broker tests for <see cref="MenuUpdatedConsumer"/>, the read-model updater (ADR-037). A snapshot that
/// cannot be applied must not be skipped: the replica would stay stale until that restaurant changed again.
/// It is retried, then quarantined on <c>menu-updated.dlq</c> (ADR-051), and the partition keeps moving so
/// the next good snapshot still lands.
/// </summary>
public class MenuUpdatedConsumerKafkaTests : IClassFixture<TadkaKafkaApiFactory>, IAsyncLifetime
{
    private readonly TadkaKafkaApiFactory _factory;
    private IProducer<string, string>? _producer;
    private IConsumer<string, string>? _dlqConsumer;

    public MenuUpdatedConsumerKafkaTests(TadkaKafkaApiFactory factory) => _factory = factory;

    public Task InitializeAsync()
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.BootstrapServers, Acks = Acks.All }).Build();

        _dlqConsumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _factory.BootstrapServers,
            GroupId = $"dlq-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        _dlqConsumer.Subscribe(Topics.MenuUpdatedDlq);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _producer?.Dispose();
        _dlqConsumer?.Close();
        _dlqConsumer?.Dispose();
        return Task.CompletedTask;
    }

    private static RestaurantSnapshotMessage Snapshot(Guid restaurantId, Guid itemId, decimal price) => new(
        Guid.NewGuid(), restaurantId, "Test Kitchen", true,
        new AddressSnapshot("1 MG Road", "", "Bengaluru", "560001", 12.97, 77.59),
        [new MenuItemSnapshot(itemId, "Test Biryani", price, "INR", true, "Mains", false)]);

    [Fact]
    public async Task A_menu_snapshot_is_applied_to_the_local_replica()
    {
        var restaurantId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var msg = Snapshot(restaurantId, itemId, 349m);

        await _producer!.ProduceAsync(Topics.MenuUpdated,
            new Message<string, string> { Key = restaurantId.ToString(), Value = JsonSerializer.Serialize(msg) });

        var price = await PollUntilAsync(() => ReplicaPriceAsync(itemId), p => p == 349m, timeoutSeconds: 25);
        Assert.Equal(349m, price);
    }

    [Fact]
    public async Task A_poison_snapshot_is_retried_then_quarantined_and_the_next_good_snapshot_still_applies()
    {
        const string poisonPayload = "{ this is not valid json ";
        var poisonKey = Guid.NewGuid().ToString();

        await _producer!.ProduceAsync(Topics.MenuUpdated,
            new Message<string, string> { Key = poisonKey, Value = poisonPayload });

        var restaurantId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var healthy = Snapshot(restaurantId, itemId, 420m);
        await _producer.ProduceAsync(Topics.MenuUpdated,
            new Message<string, string> { Key = restaurantId.ToString(), Value = JsonSerializer.Serialize(healthy) });

        var dlqMessage = PollConsumerUntil(_dlqConsumer!, cr => cr.Message.Key == poisonKey, timeoutSeconds: 30);
        Assert.NotNull(dlqMessage);

        var dlq = JsonSerializer.Deserialize<DlqMessage>(dlqMessage!.Message.Value);
        Assert.NotNull(dlq);
        Assert.Equal(Topics.MenuUpdated, dlq!.OriginalTopic);
        Assert.Equal(poisonPayload, dlq.OriginalPayload);

        var price = await PollUntilAsync(() => ReplicaPriceAsync(itemId), p => p == 420m, timeoutSeconds: 25);
        Assert.Equal(420m, price);
    }

    private async Task<decimal?> ReplicaPriceAsync(Guid itemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TadkaDbContext>();
        return await db.Set<MenuItemReplica>().AsNoTracking()
            .Where(m => m.MenuItemId == itemId).Select(m => (decimal?)m.PriceAmount).FirstOrDefaultAsync();
    }

    private static async Task<T> PollUntilAsync<T>(Func<Task<T>> fetch, Func<T, bool> isDone, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var last = default(T)!;
        while (DateTime.UtcNow < deadline)
        {
            last = await fetch();
            if (isDone(last)) return last;
            await Task.Delay(500);
        }
        return last;
    }

    private static ConsumeResult<string, string>? PollConsumerUntil(
        IConsumer<string, string> consumer, Func<ConsumeResult<string, string>, bool> match, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromSeconds(1));
                if (cr is not null && match(cr)) return cr;
            }
            catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                // The DLQ topic does not exist until the app first publishes to it. Keep polling.
                Thread.Sleep(500);
            }
        }
        return null;
    }
}
