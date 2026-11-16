// <copyright file="AetherLiveSettings.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using System.Globalization;
using System.IO;
using System.Text.Json;

internal sealed class AetherLiveSettings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly AetherLiveSettings Defaults = new();
    private static DateTime _modified;
    private static AetherLiveSettings _current = Defaults;

    public AetherLiveSettings()
    {
    }

    public string[]? GoldenTimes { get; init; }

    public string[]? TreasureTimes { get; init; }

    public int[]? TreasureLocation { get; init; }

    public int[]? BossLocation { get; init; }

    public int? BossMonster { get; init; }

    public int[]? BossReward { get; init; }

    public Dictionary<string, string>? GmMessages { get; init; }

    internal static IList<TimeOnly>? ParseSchedule(string[]? times)
    {
        if (times is null || times.Length > 24)
        {
            return null;
        }

        var schedule = new List<TimeOnly>(times.Length);
        foreach (var time in times)
        {
            if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var slot))
            {
                return null;
            }

            schedule.Add(slot);
        }

        return schedule;
    }

    internal static AetherLiveSettings Read()
    {
        if (Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME") is not { } home)
        {
            return Defaults;
        }

        lock (Gate)
        {
            try
            {
                var path = Path.Combine(home, "live-settings.json");
                var modified = File.GetLastWriteTimeUtc(path);
                if (_modified == modified)
                {
                    return _current;
                }

                if (!File.Exists(path))
                {
                    _current = Defaults;
                }
                else if (new FileInfo(path).Length <= 2048)
                {
                    var text = File.ReadAllText(path);
                    _current = JsonSerializer.Deserialize<AetherLiveSettings>(text, JsonOptions) ?? Defaults;
                }

                _modified = modified;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // Retain the last validated snapshot while an operator write is unavailable.
            }

            return _current;
        }
    }
}
