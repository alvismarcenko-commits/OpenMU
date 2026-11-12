// <copyright file="NativeDeliveryIdentityGuardTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.Tests;

using System;
using AetherMarketplaceDelivery;
using NUnit.Framework;

/// <summary>
/// Tests Marketplace native item identity verification.
/// </summary>
[TestFixture]
public sealed class NativeDeliveryIdentityGuardTests
{
    /// <summary>
    /// Matching item identity must be accepted.
    /// </summary>
    [Test]
    public void MatchingIdsAreAccepted()
    {
        var id = Guid.NewGuid();

        var request = CreateRequest(id.ToString("D"));

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.Delivered,
            id.ToString("D"));

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.True);
    }

    /// <summary>
    /// Different item identity must be rejected.
    /// </summary>
    [Test]
    public void DifferentIdsAreRejected()
    {
        var request = CreateRequest(Guid.NewGuid().ToString("D"));

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.Delivered,
            Guid.NewGuid().ToString("D"));

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.False);
    }

    /// <summary>
    /// Uncertain native delivery must not be confirmed.
    /// </summary>
    [Test]
    public void UncertainDeliveryIsRejected()
    {
        var id = Guid.NewGuid();

        var request = CreateRequest(id.ToString("D"));

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.ReconciliationRequired,
            id.ToString("D"));

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.False);
    }

    /// <summary>
    /// Full inventory must not confirm delivery.
    /// </summary>
    [Test]
    public void FullInventoryIsRejected()
    {
        var request = CreateRequest(Guid.NewGuid().ToString("D"));

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.InventoryFull,
            null);

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.False);
    }

    /// <summary>
    /// Invalid identifiers must be rejected.
    /// </summary>
    /// <param name="value">Invalid item identifier.</param>
    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void InvalidIdsAreRejected(string value)
    {
        var request = CreateRequest(value);

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.Delivered,
            Guid.NewGuid().ToString("D"));

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.False);
    }

    /// <summary>
    /// Missing native item identity must be rejected.
    /// </summary>
    [Test]
    public void MissingNativeIdentityIsRejected()
    {
        var request = CreateRequest(Guid.NewGuid().ToString("D"));

        var result = new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.Delivered,
            null);

        Assert.That(
            NativeDeliveryIdentityGuard.IsConfirmed(request, result),
            Is.False);
    }

    private static DeliveryRequest CreateRequest(string itemId)
    {
        return new DeliveryRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1001,
            "Ascension",
            itemId);
    }
}