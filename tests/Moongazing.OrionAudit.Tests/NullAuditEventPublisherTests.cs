using Moongazing.OrionAudit.Publishing;

namespace Moongazing.OrionAudit.Tests;

public class NullAuditEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_DoesNotThrow_OnEmptyList()
    {
        await NullAuditEventPublisher.Instance.PublishAsync(Array.Empty<AuditLogEvent>(), CancellationToken.None);
    }

    [Fact]
    public async Task PublishAsync_DoesNotThrow_OnPopulatedList()
    {
        var events = new[]
        {
            new AuditLogEvent(Guid.NewGuid(), "T", "1", "Inserted", DateTimeOffset.UtcNow, null, null, null, null),
        };
        await NullAuditEventPublisher.Instance.PublishAsync(events, CancellationToken.None);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(NullAuditEventPublisher.Instance, NullAuditEventPublisher.Instance);
    }
}
