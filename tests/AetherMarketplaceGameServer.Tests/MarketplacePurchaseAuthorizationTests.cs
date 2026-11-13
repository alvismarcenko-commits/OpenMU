// <copyright file="MarketplacePurchaseAuthorizationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer.Tests;

using AetherMarketplaceDelivery;

[TestFixture]
public sealed class MarketplacePurchaseAuthorizationTests
{
    private static readonly Guid PurchaseId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid CharacterId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Test]
    public void MatchingIdentityIsAccepted()
    {
        Assert.That(Check(), Is.True);
    }

    [Test]
    public void MissingPurchaseIsRejected()
    {
        Assert.That(Check(purchaseId: Guid.Empty), Is.False);
    }

    [Test]
    public void DifferentAccountIsRejected()
    {
        Assert.That(Check(liveAccountId: Guid.NewGuid()), Is.False);
    }

    [Test]
    public void DifferentCharacterIsRejected()
    {
        Assert.That(Check(liveCharacterId: Guid.NewGuid()), Is.False);
    }

    [Test]
    public void DifferentBuyerIsRejected()
    {
        Assert.That(Check(buyerCharacterId: Guid.NewGuid()), Is.False);
    }

    [Test]
    public void DifferentRealmIsRejected()
    {
        Assert.That(Check(realm: "Evolution"), Is.False);
    }

    [Test]
    public void DifferentRealmCaseIsRejected()
    {
        Assert.That(Check(realm: "ascension"), Is.False);
    }

    [Test]
    public void EmptyAccountIsRejected()
    {
        Assert.That(Check(liveAccountId: Guid.Empty), Is.False);
    }

    [Test]
    public void MissingBindingIsRejected()
    {
        Assert.That(Check(identity: null, useExplicitIdentity: true), Is.False);
    }

    [Test]
    public void InvalidMarketplaceAccountIsRejected()
    {
        Assert.That(Check(identity: ValidIdentity() with { MarketplaceAccountId = 0 }, useExplicitIdentity: true), Is.False);
    }

    private static NativeAccountIdentity ValidIdentity()
        => new(1001, AccountId, CharacterId, "Ascension");

    private static bool Check(
        Guid? purchaseId = null,
        Guid? buyerCharacterId = null,
        Guid? liveAccountId = null,
        Guid? liveCharacterId = null,
        string realm = "Ascension",
        NativeAccountIdentity? identity = null,
        bool useExplicitIdentity = false)
        => MarketplacePurchaseAuthorization.IsAuthorized(
            purchaseId ?? PurchaseId,
            buyerCharacterId ?? CharacterId,
            useExplicitIdentity ? identity : ValidIdentity(),
            liveAccountId ?? AccountId,
            liveCharacterId ?? CharacterId,
            realm);
}