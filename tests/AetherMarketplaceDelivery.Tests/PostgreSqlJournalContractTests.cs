using System;
using AetherMarketplaceDelivery;
using AetherMarketplaceDelivery.PostgreSql;
using NUnit.Framework;

namespace AetherMarketplaceDelivery.Tests;

[TestFixture]
public class PostgreSqlJournalContractTests
{
    [Test]
    public void IdenticalDeliveryIdentityMatches()
    {
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();

        var first = new DeliveryRequest(id, character, 10, "Ascension", "item-1");
        var second = new DeliveryRequest(id, character, 10, "Ascension", "item-1");

        Assert.That(DeliveryIdentityGuard.Matches(first, second), Is.True);
    }

    [Test]
    public void ChangedCharacterIsRejected()
    {
        var first = NewRequest();
        var second = first with { CharacterId = Guid.NewGuid() };

        Assert.That(DeliveryIdentityGuard.Matches(first, second), Is.False);
        Assert.Throws<InvalidOperationException>(
            () => DeliveryIdentityGuard.RequireMatch(first, second));
    }

    [Test]
    public void ChangedAccountIsRejected()
    {
        var first = NewRequest();
        var second = first with { AccountId = 999 };

        Assert.That(DeliveryIdentityGuard.Matches(first, second), Is.False);
    }

    [Test]
    public void ChangedRealmIsRejected()
    {
        var first = NewRequest();
        var second = first with { Realm = "Origin" };

        Assert.That(DeliveryIdentityGuard.Matches(first, second), Is.False);
    }

    [Test]
    public void ChangedItemIsRejected()
    {
        var first = NewRequest();
        var second = first with { ItemInstanceId = "different-item" };

        Assert.That(DeliveryIdentityGuard.Matches(first, second), Is.False);
    }

    [Test]
    public void ClaimRequiresPendingState()
    {
        Assert.That(DeliveryJournalSql.Claim,
            Does.Contain("state = 'PENDING'"));
        Assert.That(DeliveryJournalSql.Claim,
            Does.Contain("state = 'PROCESSING'"));
    }

    [Test]
    public void ConfirmationRequiresProcessingState()
    {
        Assert.That(DeliveryJournalSql.ConfirmDelivered,
            Does.Contain("state = 'PROCESSING'"));
        Assert.That(DeliveryJournalSql.ConfirmDelivered,
            Does.Contain("state = 'DELIVERED'"));
    }

    [Test]
    public void ReconciliationRequiresProcessingState()
    {
        Assert.That(DeliveryJournalSql.RequireReconciliation,
            Does.Contain("state = 'PROCESSING'"));
        Assert.That(DeliveryJournalSql.RequireReconciliation,
            Does.Contain("state = 'RECONCILIATION_REQUIRED'"));
    }

    [Test]
    public void RegistrationUsesConflictProtection()
    {
        Assert.That(DeliveryJournalSql.Insert,
            Does.Contain("ON CONFLICT DO NOTHING"));
    }

    private static DeliveryRequest NewRequest()
        => new(Guid.NewGuid(), Guid.NewGuid(), 10, "Ascension", "item-1");
}