using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tadka.Payment.Api.Data;
using Tadka.Payment.Api.Messaging;

namespace Tadka.Payment.Api.Tests;

/// <summary>ADR-028 Payment Outbox: payment-results / payment-refunded are staged, not dual-written to Kafka.</summary>
public class PaymentOutboxTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    [Fact]
    public async Task Outbox_table_accepts_staged_payment_results_row()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var msg = new PaymentResultMessage(Guid.NewGuid(), Guid.NewGuid(), "Completed", "ref-1", null);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = Topics.PaymentResults,
            Key = msg.OrderId.ToString(),
            Payload = JsonSerializer.Serialize(msg)
        });
        await db.SaveChangesAsync();

        var row = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(o => o.Key == msg.OrderId.ToString() && o.Topic == Topics.PaymentResults);
        Assert.Null(row.ProcessedAt);
        Assert.Contains("Completed", row.Payload);
    }
}
