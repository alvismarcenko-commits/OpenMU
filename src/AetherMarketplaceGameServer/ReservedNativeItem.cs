// <copyright file="ReservedNativeItem.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using AetherMarketplaceDelivery;
using MUnique.OpenMU.Interfaces;

/// <summary>A server-owned immutable purchase snapshot, never populated from delivery request fields.</summary>
/// <param name="Request">The reserved delivery identity.</param>
/// <param name="Identity">The independently verified account binding.</param>
/// <param name="Specification">The purchased item specification.</param>
public sealed record ReservedNativeItem(DeliveryRequest Request, NativeAccountIdentity Identity, ItemSpecification Specification);
