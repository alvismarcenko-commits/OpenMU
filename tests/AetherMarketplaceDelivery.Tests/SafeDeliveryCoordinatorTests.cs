using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AetherMarketplaceDelivery;
using NUnit.Framework;

namespace AetherMarketplaceDelivery.Tests;

[TestFixture]
public class SafeDeliveryCoordinatorTests
{
    [Test]
    public async Task SuccessfulDelivery()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(journal, native);

        var result = await coordinator.DeliverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "item-1");

        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.Delivered));
        Assert.That(native.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task DuplicateDeliveryIsBlocked()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(journal, native);
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();

        var first = await coordinator.DeliverAsync(id, character, "item-1");
        var second = await coordinator.DeliverAsync(id, character, "item-1");

        Assert.That(first, Is.EqualTo(SafeDeliveryStatus.Delivered));
        Assert.That(second, Is.EqualTo(SafeDeliveryStatus.AlreadyDelivered));
        Assert.That(native.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidDeliveryIdIsRejected()
    {
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);

        var result = await coordinator.DeliverAsync(
            Guid.Empty, Guid.NewGuid(), "item-1");

        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.Rejected));
        Assert.That(native.Calls, Is.Zero);
    }

    [Test]
    public async Task InvalidCharacterIdIsRejected()
    {
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);

        var result = await coordinator.DeliverAsync(
            Guid.NewGuid(), Guid.Empty, "item-1");

        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.Rejected));
        Assert.That(native.Calls, Is.Zero);
    }

    [Test]
    public async Task EmptyItemIdIsRejected()
    {
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);

        var result = await coordinator.DeliverAsync(
            Guid.NewGuid(), Guid.NewGuid(), " ");

        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.Rejected));
        Assert.That(native.Calls, Is.Zero);
    }

    [Test]
    public async Task ChangedRecipientIsRejected()
    {
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);
        var id = Guid.NewGuid();

        await coordinator.DeliverAsync(id, Guid.NewGuid(), "item-1");

        var result = await coordinator.DeliverAsync(
            id, Guid.NewGuid(), "item-1");

        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.Rejected));
        Assert.That(native.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task NativeFailureRequiresReconciliation()
    {
        var native = new FakeNative { Fail = true };
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);

        var result = await coordinator.DeliverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "item-1");

        Assert.That(result,
            Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
    }

    [Test]
    public async Task ConcurrentRequestsDeliverOnlyOnce()
    {
        var native = new FakeNative();
        var coordinator = new SafeDeliveryCoordinator(new MemoryJournal(), native);
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();

        var tasks = new Task<SafeDeliveryStatus>[20];

        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = coordinator.DeliverAsync(
                id, character, "item-1").AsTask();
        }

        var results = await Task.WhenAll(tasks);

        Assert.That(native.Calls, Is.EqualTo(1));
        Assert.That(
            Array.FindAll(results, x => x == SafeDeliveryStatus.Delivered).Length,
            Is.EqualTo(1));
        Assert.That(
            Array.FindAll(results, x => x == SafeDeliveryStatus.AlreadyDelivered).Length,
            Is.EqualTo(19));
    }

    private sealed class MemoryJournal : ISafeDeliveryJournal
    {
        private readonly ConcurrentDictionary<Guid, SafeDeliveryReceipt> entries = new();

        public ValueTask<SafeDeliveryReceipt?> FindAsync(
            Guid id, CancellationToken ct = default)
        {
            entries.TryGetValue(id, out var receipt);
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<bool> TryReserveAsync(
            Guid id, Guid character, string item, CancellationToken ct = default)
        {
            return ValueTask.FromResult(
                entries.TryAdd(id, new SafeDeliveryReceipt(
                    id, character, item,
                    SafeDeliveryStatus.ReconciliationRequired)));
        }

        public ValueTask MarkAsync(
            SafeDeliveryReceipt receipt, CancellationToken ct = default)
        {
            entries[receipt.DeliveryId] = receipt;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeNative : ISafeNativeDelivery
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public bool Fail { get; set; }

        public ValueTask<SafeDeliveryStatus> DeliverAsync(
            Guid id, Guid character, string item, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls);

            return ValueTask.FromResult(
                Fail
                    ? SafeDeliveryStatus.ReconciliationRequired
                    : SafeDeliveryStatus.Delivered);
        }
    }
}