// <copyright file="LoginServer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.LoginServer;

using System.Threading;
using MUnique.OpenMU.Interfaces;
using Nito.AsyncEx;

/// <summary>
/// The login server which keeps track of connected accounts in-memory.
/// </summary>
public class LoginServer : ILoginServer
{
    private readonly Dictionary<string, byte> _connectedAccounts = new();

    private readonly Dictionary<string, string> _connectedAddresses = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _limitAscensionConnections = Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true";

    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);

    private readonly AsyncLock _syncRoot = new();

    private readonly TimeProvider _time;
    private int _activeAccountCount;

    /// <summary>Initializes a new instance of the <see cref="LoginServer"/> class.</summary>
    public LoginServer()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LoginServer"/> class with an overridable clock.</summary>
    /// <param name="time">The clock used for administrative reservation expiry.</param>
    public LoginServer(TimeProvider time) => this._time = time;

    /// <summary>Gets the number of accounts currently admitted to the login server.</summary>
    public int ActiveAccountCount => Volatile.Read(ref this._activeAccountCount);

    /// <inheritdoc />
    public async ValueTask<bool> TryReserveAccountAsync(string accountName, int reservationId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reservationId, byte.MaxValue);
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        this.RemoveExpiredReservation(accountName);
        if (this._connectedAccounts.ContainsKey(accountName) || (this._reservations.TryGetValue(accountName, out var existing) && existing.Id != reservationId))
        {
            return false;
        }

        this._reservations[accountName] = new Reservation(reservationId, this._time.GetUtcNow().AddMinutes(5));
        return true;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAccountReservationAsync(string accountName, int reservationId)
    {
        using var gate = await this._syncRoot.LockAsync().ConfigureAwait(false);
        if (this._reservations.TryGetValue(accountName, out var existing) && existing.Id == reservationId)
        {
            this._reservations.Remove(accountName);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<Dictionary<string, byte>> GetSnapshotAsync()
    {
        using var l = await this._syncRoot.LockAsync();
        return new(this._connectedAccounts);
    }

    /// <inheritdoc/>
    public Task<bool> TryLoginAsync(string accountName, byte serverId)
        => this.TryLoginCoreAsync(accountName, serverId, null, enforceAddressLimit: false);

    /// <inheritdoc />
    public Task<bool> TryLoginFromAddressAsync(string accountName, byte serverId, string? address)
        => this.TryLoginCoreAsync(accountName, serverId, address, this._limitAscensionConnections);

    private async Task<bool> TryLoginCoreAsync(string accountName, byte serverId, string? address, bool enforceAddressLimit)
    {
        using var l = await this._syncRoot.LockAsync().ConfigureAwait(false);
        this.RemoveExpiredReservation(accountName);
        if (this._connectedAccounts.ContainsKey(accountName) || this._reservations.ContainsKey(accountName))
        {
            return false;
        }

        if (enforceAddressLimit)
        {
            if (string.IsNullOrEmpty(address) || this._connectedAddresses.Values.Count(value => value == address) >= 2)
            {
                return false;
            }

            this._connectedAddresses.Add(accountName, address);
        }

        this._connectedAccounts.Add(accountName, serverId);
        Volatile.Write(ref this._activeAccountCount, this._connectedAccounts.Count);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask LogOffAsync(string accountName, byte serverId)
    {
        using var l = await this._syncRoot.LockAsync();

        // A late log off of another server must not remove the current login of the account.
        if (this._connectedAccounts.TryGetValue(accountName, out var currentServerId) && currentServerId == serverId)
        {
            this._connectedAccounts.Remove(accountName);
            this._connectedAddresses.Remove(accountName);
            Volatile.Write(ref this._activeAccountCount, this._connectedAccounts.Count);
        }
    }

    private void RemoveExpiredReservation(string accountName)
    {
        if (this._reservations.TryGetValue(accountName, out var reservation) && reservation.ExpiresAt <= this._time.GetUtcNow())
        {
            this._reservations.Remove(accountName);
        }
    }

    private sealed record Reservation(int Id, DateTimeOffset ExpiresAt);
}
