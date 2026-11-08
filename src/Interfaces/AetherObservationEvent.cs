// <copyright file="AetherObservationEvent.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Interfaces;

/// <summary>A sanitized observation emitted by the Ascension game runtime.</summary>
/// <param name="ServerId">The game server.</param>
/// <param name="TimestampUtc">The observation time.</param>
/// <param name="Kind">The allowlisted observation kind.</param>
/// <param name="Code">The allowlisted observation code.</param>
public sealed record AetherObservationEvent(byte ServerId, DateTime TimestampUtc, string Kind, string Code)
    : GameEvent(ServerId, TimestampUtc);
