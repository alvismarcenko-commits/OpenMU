// <copyright file="IAtomicCharacterProgressContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

using System.Threading;
using MUnique.OpenMU.DataModel.Entities;

/// <summary>
/// Saves a character exchange atomically against its persisted version.
/// </summary>
public interface IAtomicCharacterProgressContext
{
    /// <summary>
    /// Saves all tracked progress under a database character lock, rejecting stale snapshots.
    /// A failure invalidates the context: dispose it and reload before further writes.
    /// </summary>
    /// <param name="character">The tracked character being exchanged.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task which completes after commit.</returns>
    ValueTask SaveCharacterExchangeAsync(Character character, CancellationToken cancellationToken = default);
}
