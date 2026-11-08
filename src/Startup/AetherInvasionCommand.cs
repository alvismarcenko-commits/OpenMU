// <copyright file="AetherInvasionCommand.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace MUnique.OpenMU.Startup;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;

internal static class AetherInvasionCommand
{
    internal static async Task RunAsync(string command, IDictionary<int, IGameServer> servers, CancellationToken token)
    {
        var home = Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME");
        var parts = command.Split(' ', 3, StringSplitOptions.None);
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true"
            || home is null || !Path.IsPathFullyQualified(home)
            || parts is not [var operation, var requestId]
            || operation is not ("aether-golden-invasion" or "aether-golden-stop" or "aether-treasure-start" or "aether-treasure-stop")
            || !Guid.TryParseExact(requestId, "N", out _))
        {
            return;
        }

        var receipts = Path.Combine(home, "invasion-requests");
        try
        {
            Directory.CreateDirectory(receipts);
            var file = Path.Combine(receipts, requestId + ".json");
            // Create-only before dispatch. A replay cannot invoke ForceStart twice.
            await using (var marker = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(marker, new { status = "pending" }, cancellationToken: token).ConfigureAwait(false);
            }

            var status = "unavailable";
            if (File.Exists(Path.Combine(home, "STOP")) && operation is "aether-golden-invasion" or "aether-treasure-start")
            {
                status = "emergency_disabled";
            }
            else if (servers.TryGetValue(0, out var instance) && instance is GameServer { ServerState: ServerState.Started } server)
            {
                var plugins = server.Context.PlugInManager.GetActivePlugInsOf<IPeriodicTaskPlugIn>();
                if (operation.StartsWith("aether-golden-", StringComparison.Ordinal)
                    && plugins.OfType<GoldenInvasionPlugIn>().SingleOrDefault() is { } golden)
                {
                    status = operation == "aether-golden-stop"
                        ? await golden.RequestOperatorStopAsync(server.Context).ConfigureAwait(false)
                        : await golden.RequestOperatorStartAsync(server.Context).ConfigureAwait(false) ? "requested" : "active_or_disabled";
                }
                else if (operation.StartsWith("aether-treasure-", StringComparison.Ordinal)
                    && plugins.OfType<AetherTreasureHuntPlugIn>().SingleOrDefault() is { } treasure)
                {
                    status = await treasure.OperateAsync(server.Context, operation == "aether-treasure-start").ConfigureAwait(false);
                }
            }

            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { status, request_id = requestId }), token).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Command failure stays local. The game and existing invasion schedule continue.
        }
    }
}
