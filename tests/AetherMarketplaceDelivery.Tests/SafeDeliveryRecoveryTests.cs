using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AetherMarketplaceDelivery;
using NUnit.Framework;

namespace AetherMarketplaceDelivery.Tests;

[TestFixture]
public class SafeDeliveryRecoveryTests
{
    [Test]
    public async Task NativeExceptionDoesNotRetry()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative { Throw = true };
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();
        var coordinator = new SafeDeliveryCoordinator(journal, native);
        var first = await coordinator.DeliverAsync(id, character, "item");
        var second = await coordinator.DeliverAsync(id, character, "item");
        Assert.That(first, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
        Assert.That(second, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
        Assert.That(native.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task NewCoordinatorDoesNotRetry()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative { Throw = true };
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();
        await new SafeDeliveryCoordinator(journal, native)
            .DeliverAsync(id, character, "item");
        native.Throw = false;
        var result = await new SafeDeliveryCoordinator(journal, native)
            .DeliverAsync(id, character, "item");
        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
        Assert.That(native.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ConfirmedReceiptBlocksDuplicate()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative();
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();
        await journal.MarkAsync(new SafeDeliveryReceipt(
            id, character, "item", SafeDeliveryStatus.Delivered));
        var result = await new SafeDeliveryCoordinator(journal, native)
            .DeliverAsync(id, character, "item");
        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.AlreadyDelivered));
        Assert.That(native.Calls, Is.Zero);
    }

    [Test]
    public async Task UnconfirmedReservationBlocksRetry()
    {
        var journal = new MemoryJournal();
        var native = new FakeNative();
        var id = Guid.NewGuid();
        var character = Guid.NewGuid();
        await journal.TryReserveAsync(id, character, "item");
        var result = await new SafeDeliveryCoordinator(journal, native)
            .DeliverAsync(id, character, "item");
        Assert.That(result, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
        Assert.That(native.Calls, Is.Zero);
    }

    private sealed class MemoryJournal : ISafeDeliveryJournal
    {
        private readonly ConcurrentDictionary<Guid, SafeDeliveryReceipt> entries = new();

        public ValueTask<SafeDeliveryReceipt?> FindAsync(Guid id, CancellationToken ct = default)
        {
            entries.TryGetValue(id, out var receipt);
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<bool> TryReserveAsync(Guid id, Guid character, string item, CancellationToken ct = default)
            => ValueTask.FromResult(entries.TryAdd(id,
                new SafeDeliveryReceipt(id, character, item, SafeDeliveryStatus.ReconciliationRequired)));

        public ValueTask MarkAsync(SafeDeliveryReceipt receipt, CancellationToken ct = default)
        {
            entries[receipt.DeliveryId] = receipt;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeNative : ISafeNativeDelivery
    {
        private int calls;
        public bool Throw { get; set; }
        public int Calls => Volatile.Read(ref calls);

        public ValueTask<SafeDeliveryStatus> DeliverAsync(
            Guid id, Guid character, string item, CancellationToken ct = default)
        {
            Interlocked.Increment(ref calls);
            if (Throw) throw new InvalidOperationException("Simulated failure");
            return ValueTask.FromResult(SafeDeliveryStatus.Delivered);
        }
    }
}