// <copyright file="AetherEventEmergency.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using System.IO;

internal static class AetherEventEmergency
{
    internal static bool IsDisabled()
        => Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true"
           && Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME") is { } home
           && File.Exists(Path.Combine(home, "STOP"));
}
