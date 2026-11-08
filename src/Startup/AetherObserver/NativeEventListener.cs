// <copyright file="NativeEventListener.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace Aether.OpenMU.Observer;

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Interfaces;

/// <summary>Bounded, non-blocking observation of native events. No game write API.</summary>
public sealed class NativeEventListener : IGameEventListener, IAsyncDisposable, IDisposable
{
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(256);
    private readonly byte[] _key;
    private readonly string _directory;
    private readonly string _realm;
    private readonly string _server;
    private readonly string _sourceSha;
    private readonly bool _enabled;
    private readonly string _stopFile;
    private readonly Task _worker;
    private long _dropped;
    private int _disposed;

    /// <summary>Creates a disabled-by-default listener. The caller owns restricted spool ACLs.</summary>
    public NativeEventListener(byte[] key, string directory, string realm, byte server,
        string sourceSha, string stopFile, bool enabled = false)
    {
        if (key.Length != 32 || sourceSha.Length != 40 || sourceSha.Any(c => !"0123456789abcdef".Contains(c))
            || !new[] { "origin", "legacy", "evolution", "ascension" }.Contains(realm))
        {
            throw new ArgumentException("Invalid producer scope or key.");
        }

        this._key = key.ToArray();
        this._directory = Path.GetFullPath(directory);
        this._realm = realm;
        this._server = server.ToString(System.Globalization.CultureInfo.InvariantCulture);
        this._sourceSha = sourceSha;
        this._stopFile = Path.GetFullPath(stopFile);
        this._enabled = enabled;
        this._worker = this.WriteFramesAsync();
    }

    /// <summary>Gets dropped observations. Any increase requires an operator alert.</summary>
    public long DroppedFrames => Interlocked.Read(ref this._dropped);

    /// <inheritdoc />
    public ValueTask OnGameEventAsync(GameEvent gameEvent)
    {
        // Names, map strings, accounts and chat are deliberately not exported.
        switch (gameEvent)
        {
            case InvasionStartedEvent started:
                this.Queue("invasion_started", started.ServerId, started.TimestampUtc,
                    new { plugin_id = started.InvasionId.ToString("D") });
                break;
            case InvasionEndedEvent ended:
                this.Queue("invasion_ended", ended.ServerId, ended.TimestampUtc,
                    new { plugin_id = ended.InvasionId.ToString("D") });
                break;
            case PlayerEnteredGameEvent entered:
                this.Queue("player_entered", entered.ServerId, entered.TimestampUtc, new { });
                break;
            case AccountLoginBlockedEvent blocked:
                this.Queue("security", blocked.ServerId, blocked.TimestampUtc, new { code = "account_login_blocked" });
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Observes the native server connection count without exporting identities.</summary>
    public void ObserveConnections(DateTime utcNow, int count)
    {
        if (count >= 0)
        {
            this.Queue("connections", byte.Parse(this._server, System.Globalization.CultureInfo.InvariantCulture),
                utcNow, new { count });
        }
    }

    /// <summary>Reads the native scheduler. It never creates a timer or starts an event.</summary>
    /// <remarks>Pass only active, explicitly allowed plugins for this server, using its actual time zone.
    /// The host refreshes snapshots at most every 60 seconds. One plugin can represent a single-target
    /// Treasure Hunt; this adapter never invents a treasure-winner signal.</remarks>
    public void ObserveCalendar(IReadOnlyDictionary<Guid, IScheduledPlugIn> activePlugins,
        DateTime utcNow, TimeZoneInfo serverTimeZone)
    {
        if (utcNow.Kind != DateTimeKind.Utc || activePlugins.Count > 200)
        {
            throw new ArgumentException("Bounded active plugin set and UTC required.");
        }

        var events = activePlugins.Select(entry => (entry.Key,
                Start: entry.Value.GetNextStartUtc(utcNow, serverTimeZone)))
            .Where(entry => entry.Start is { Kind: DateTimeKind.Utc } start && start > utcNow)
            .Select(entry => new
            {
                id = entry.Key.ToString("N") + ":" + entry.Start!.Value.Ticks,
                starts_at = entry.Start!.Value.ToString("O"),
            }).ToArray();
        this.Queue("calendar", byte.Parse(this._server, System.Globalization.CultureInfo.InvariantCulture),
            utcNow, new { events });
    }

    /// <summary>Exports only sanitized host health/error codes. Not raw exception text.</summary>
    public void ObserveStatus(DateTime utcNow, string kind, string code)
    {
        if (kind is not ("health" or "error" or "security") || code.Length is < 1 or > 100
            || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-'))
            || (kind == "health" && code is not ("healthy" or "unhealthy" or "unknown")))
        {
            throw new ArgumentException("Invalid status code.");
        }

        this.Queue(kind, byte.Parse(this._server, System.Globalization.CultureInfo.InvariantCulture), utcNow, new { code });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) != 0) { return; }
        this._frames.Writer.TryComplete();
        await this._worker.ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(this._key);
    }

    /// <inheritdoc />
    public void Dispose() => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void Queue(string kind, byte server, DateTime utcNow, object data)
    {
        if (Volatile.Read(ref this._disposed) != 0 || !this._enabled || File.Exists(this._stopFile)
            || server.ToString(System.Globalization.CultureInfo.InvariantCulture) != this._server)
        {
            return;
        }

        if (utcNow.Kind != DateTimeKind.Utc)
        {
            Interlocked.Increment(ref this._dropped);
            return;
        }

        // Stable native replay identity: plugin + server + timestamp + kind + sanitized data.
        // This is not an invented shared start/end occurrence or reward entitlement.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { kind, server, utcNow, data });
        var id = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
        var content = new { version = 1, id, at = utcNow.ToString("O"),
            realm = this._realm, server = this._server, source_sha = this._sourceSha, kind, data };
        var payload = JsonSerializer.SerializeToUtf8Bytes(content);
        // Maximum base64 frame remains below the consumer's 64 KiB limit.
        if (payload.Length > 45000 || !this._frames.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref this._dropped);
        }
    }

    private async Task WriteFramesAsync()
    {
        await foreach (var payload in this._frames.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (File.Exists(this._stopFile))
            {
                continue;
            }

            string? temporary = null;
            try
            {
                Directory.CreateDirectory(this._directory);
                // Fail closed on disk pressure; no unbounded producer spool.
                if (Directory.EnumerateFiles(this._directory).Take(10000).Count() >= 10000)
                {
                    Interlocked.Increment(ref this._dropped);
                    continue;
                }

                var envelope = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    payload = Convert.ToBase64String(payload),
                    mac = Convert.ToHexString(HMACSHA256.HashData(this._key, payload)).ToLowerInvariant(),
                });
                var name = Guid.NewGuid().ToString("N");
                temporary = Path.Combine(this._directory, name + ".tmp");
                await File.WriteAllBytesAsync(temporary, envelope).ConfigureAwait(false);
                File.Move(temporary, Path.Combine(this._directory, name + ".json"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // No exception may escape into the game event publisher. No raw logs or credentials.
                Interlocked.Increment(ref this._dropped);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref this._dropped);
                    }
                }
            }
        }
    }
}
