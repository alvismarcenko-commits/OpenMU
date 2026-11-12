// <copyright file="NativeAccountIdentityBridgeTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.Tests;

using System;
using NUnit.Framework;

/// <summary>
/// Tests strict Marketplace and OpenMU identity validation.
/// </summary>
[TestFixture]
public sealed class NativeAccountIdentityBridgeTests
{
    private static readonly Guid Account =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Character =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>
    /// Valid identity is accepted.
    /// </summary>
    [Test]
    public void MatchingIdentityIsAccepted()
    {
        Assert.That(
            Check(),
            Is.True);
    }

    /// <summary>
    /// Different Marketplace account is rejected.
    /// </summary>
    [Test]
    public void DifferentMarketplaceAccountIsRejected()
    {
        Assert.That(
            Check(marketplaceAccount: 2002),
            Is.False);
    }

    /// <summary>
    /// Different OpenMU account is rejected.
    /// </summary>
    [Test]
    public void DifferentOpenMuAccountIsRejected()
    {
        Assert.That(
            Check(liveAccount: Guid.NewGuid()),
            Is.False);
    }

    /// <summary>
    /// Different character is rejected.
    /// </summary>
    [Test]
    public void DifferentCharacterIsRejected()
    {
        Assert.That(
            Check(liveCharacter: Guid.NewGuid()),
            Is.False);
    }

    /// <summary>
    /// Different request character is rejected.
    /// </summary>
    [Test]
    public void ChangedRequestCharacterIsRejected()
    {
        Assert.That(
            Check(requestCharacter: Guid.NewGuid()),
            Is.False);
    }

    /// <summary>
    /// Cross-realm delivery is rejected.
    /// </summary>
    [Test]
    public void DifferentRealmIsRejected()
    {
        Assert.That(
            Check(liveRealm: "Evolution"),
            Is.False);
    }

    /// <summary>
    /// Empty OpenMU account is rejected.
    /// </summary>
    [Test]
    public void EmptyOpenMuAccountIsRejected()
    {
        Assert.That(
            Check(liveAccount: Guid.Empty),
            Is.False);
    }

    /// <summary>
    /// Empty delivery identifier is rejected.
    /// </summary>
    [Test]
    public void EmptyDeliveryIdIsRejected()
    {
        Assert.That(
            Check(deliveryId: Guid.Empty),
            Is.False);
    }

    /// <summary>
    /// Invalid Marketplace account is rejected.
    /// </summary>
    [Test]
    public void InvalidMarketplaceAccountIsRejected()
    {
        Assert.That(
            Check(identityAccount: 0),
            Is.False);
    }

    /// <summary>
    /// Realm identifiers are case-sensitive.
    /// </summary>
    [Test]
    public void RealmCaseMismatchIsRejected()
    {
        Assert.That(
            Check(liveRealm: "ascension"),
            Is.False);
    }

    private static bool Check(
        long marketplaceAccount = 1001,
        long identityAccount = 1001,
        Guid? liveAccount = null,
        Guid? liveCharacter = null,
        Guid? requestCharacter = null,
        Guid? deliveryId = null,
        string liveRealm = "Ascension")
    {
        var request = new DeliveryRequest(
            deliveryId ?? Guid.NewGuid(),
            requestCharacter ?? Character,
            marketplaceAccount,
            "Ascension",
            Guid.NewGuid().ToString("D"));

        var identity = new NativeAccountIdentity(
            identityAccount,
            Account,
            Character,
            "Ascension");

        return NativeAccountIdentityBridge.Matches(
            request,
            identity,
            liveAccount ?? Account,
            liveCharacter ?? Character,
            liveRealm);
    }
}