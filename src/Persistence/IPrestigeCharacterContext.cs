// Licensed under MIT. AetherMU Europe; PostgreSQL staging candidate.
namespace MUnique.OpenMU.Persistence;
using System.Threading;
using MUnique.OpenMU.DataModel.Entities;
/// <summary>Native character mutation and APP receipt in one database transaction.</summary>
public interface IPrestigeCharacterContext
{
    /// <summary>Reads the approved character's Grand Reset count and APP balance without modifying progress.</summary>
    ValueTask<(string GrandResets, string Balance)?> ReadPrestigeAsync(Character character, string realm, CancellationToken cancellationToken = default);
    /// <summary>Commits tracked native progress with APP; failure poisons the context.</summary>
    ValueTask<string> ExecutePrestigeExchangeAsync(Character character, string realm, string operationId, int resetsBefore, CancellationToken cancellationToken = default);
    /// <summary>Deliver a reserved reward and its receipt in the native inventory transaction. Callback must create one validated, tracked native item.</summary>
    ValueTask<string> DeliverPrestigeRewardAsync(Character character, string realm, Guid purchaseId, Func<int, int, CancellationToken, ValueTask<Item>> createItem, CancellationToken cancellationToken = default);
}
