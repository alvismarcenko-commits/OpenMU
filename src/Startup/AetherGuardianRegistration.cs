// <copyright file="AetherGuardianRegistration.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace MUnique.OpenMU.Startup;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Aether.OpenMU.Observer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;

internal static class AetherGuardianRegistration
{
    internal static void Add(IServiceCollection services)
    {
        var home = Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME");
        var keyHex = Environment.GetEnvironmentVariable("AETHER_OBSERVER_KEY_HEX");
        var sourceSha = Environment.GetEnvironmentVariable("AETHER_OBSERVER_SOURCE_SHA");
        if (home is null || keyHex is null || sourceSha is null
            || Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true"
            || !Path.IsPathFullyQualified(home) || keyHex.Length != 64 || !keyHex.All(char.IsAsciiHexDigit)
            || sourceSha.Length != 40 || !sourceSha.All(c => "0123456789abcdef".Contains(c)))
        {
            return;
        }

        const byte serverId = 0;
        services.AddAetherObserver(
            _ => new NativeEventListener(Convert.FromHexString(keyHex), Path.Combine(home, "spool"), "ascension",
                serverId, sourceSha, Path.Combine(home, "STOP"), enabled: true),
            provider =>
            {
                var servers = provider.GetRequiredService<IDictionary<int, IGameServer>>();
                if (!servers.TryGetValue(serverId, out var instance) || instance is not GameServer server)
                {
                    return new ObserverSnapshot(ServerState.Stopped, new Dictionary<Guid, IScheduledPlugIn>(), TimeZoneInfo.Utc);
                }

                var active = server.Context.PlugInManager.GetActivePlugInsOf<IPeriodicTaskPlugIn>()
                    .OfType<IScheduledPlugIn>()
                    .Select(plugin => (Plugin: plugin, Identity: plugin.GetType().GetCustomAttribute<GuidAttribute>()))
                    .Where(entry => entry.Identity is not null)
                    .ToDictionary(entry => Guid.Parse(entry.Identity!.Value), entry => entry.Plugin);
                return new ObserverSnapshot(server.ServerState, active, server.Context.ServerTimeZone, server.CurrentConnections);
            },
            TimeSpan.FromSeconds(10));
        services.AddSingleton<ILoggerProvider>(provider => new ObserverLoggerProvider(provider.GetRequiredService<NativeEventListener>()));
    }
}
