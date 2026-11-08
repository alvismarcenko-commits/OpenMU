namespace Aether.OpenMU.Observer;

using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Interfaces;

/// <summary>A snapshot read by the host; plugins must already be server-filtered and active.</summary>
public sealed record ObserverSnapshot(ServerState State,
    IReadOnlyDictionary<Guid, IScheduledPlugIn> ActivePlugins, TimeZoneInfo TimeZone, int? Connections = null, int? Sessions = null);

/// <summary>Explicit opt-in composition. Never starts a game server or modifies native configuration.</summary>
public static class ObserverHosting
{
    public static IServiceCollection AddAetherObserver(this IServiceCollection services,
        Func<IServiceProvider, NativeEventListener> createListener,
        Func<IServiceProvider, ObserverSnapshot> readSnapshot, TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (services.Any(d => d.ServiceType == typeof(NativeEventListener)))
        {
            throw new InvalidOperationException("Observer already registered.");
        }

        services.AddSingleton(createListener);
        // GameServerContainer consumes IEnumerable<IGameEventListener> in OpenMU 766da9a12.
        services.AddSingleton<IGameEventListener>(s => s.GetRequiredService<NativeEventListener>());
        services.AddSingleton<IHostedService>(s => new ObserverWorker(
            s.GetRequiredService<NativeEventListener>(), () => readSnapshot(s), interval));
        return services;
    }
}

/// <summary>Only reads state and the native schedule. A failed read becomes a sanitized error.</summary>
public sealed class ObserverWorker(NativeEventListener observer, Func<ObserverSnapshot> readSnapshot,
    TimeSpan interval) : BackgroundService
{
    private bool _announcedStart;

    public void PollOnce(DateTime utcNow)
    {
        try
        {
            var snapshot = readSnapshot();
            observer.ObserveStatus(utcNow, "health", snapshot.State switch
            {
                ServerState.Started => "healthy",
                ServerState.Timeout or ServerState.Stopped => "unhealthy",
                _ => "unknown",
            });
            if (snapshot.Connections is { } count)
            {
                observer.ObserveConnections(utcNow, count);
            }
            if (snapshot.Sessions is { } sessions)
            {
                observer.ObserveSessions(utcNow, sessions);
            }
            if (snapshot.State == ServerState.Started && !this._announcedStart)
            {
                observer.ObserveStatus(utcNow, "lifecycle", "server_started");
                this._announcedStart = true;
            }
            observer.ObserveCalendar(snapshot.ActivePlugins, utcNow, snapshot.TimeZone);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            observer.ObserveStatus(utcNow, "error", "snapshot_read_failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do { this.PollOnce(DateTime.UtcNow); }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

/// <summary>Optional OpenMU logging sink. Never exports messages, categories or exception text.</summary>
public sealed class ObserverLoggerProvider(NativeEventListener observer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SanitizedLogger(observer);
    public void Dispose() { }

    private sealed class SanitizedLogger(NativeEventListener observer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (this.IsEnabled(logLevel))
            {
                observer.ObserveStatus(DateTime.UtcNow, "error", "host_error_" +
                    eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
