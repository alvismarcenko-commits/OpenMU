// Licensed under the MIT License. Native local staging worker; never an internet-facing registration API.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.Json;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Network;
using Npgsql;
using MUnique.OpenMU.GameLogic.PlugIns.GameEvents;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.PlugIns;

var dsn = Environment.GetEnvironmentVariable("AETHER_NATIVE_DSN") ?? throw new InvalidOperationException("AETHER_NATIVE_DSN missing");
await AetherStagingState.VerifyOwnerAsync(dsn);
// Match the native Startup serializer setup before initialization or cached configuration reads.
JsonConverterRegistry.RegisterConverter(new LocalizedStringJsonConverter());
JsonConverterRegistry.RegisterConverter(new BinaryAsHexJsonConverter());
var accounts = new AetherAccountService(dsn); // Guard database and loopback before any I/O.
if (args.SequenceEqual(new[] { "initialize-empty" }))
{
    // Dedicated DB must already exist; no create/drop database, role or production changes.
    await using var connection = new NpgsqlConnection(dsn);
    await connection.OpenAsync();
    await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(17863001)", connection);
    if (await claim.ExecuteScalarAsync() is not true) { throw new InvalidOperationException("Initialization already running."); }
    await using var empty = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema')", connection);
    if (Convert.ToInt64(await empty.ExecuteScalarAsync()) != 0) { throw new InvalidOperationException("Refuse nonempty database. No repair, overwrite or migration of historical data."); }
    foreach (var key in new[] { "DB_HOST", "DB_ADMIN_USER", "DB_ADMIN_PW" })
    { if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) { throw new InvalidOperationException("Inherited DB override refused."); } }
    var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ConnectionSettings.xml"));
    var strings = xml.Descendants().Where(x => x.Name.LocalName == "ConnectionString").Select(x => x.Value).ToList();
    var expected = new NpgsqlConnectionStringBuilder(dsn).ConnectionString;
    if (strings.Count != 8 || strings.Any(x => new NpgsqlConnectionStringBuilder(x).ConnectionString != expected))
    { throw new InvalidOperationException("All eight native contexts must target this isolated database with the supplied dedicated role."); }
    await new ConfigFileDatabaseConnectionStringProvider().InitializeAsync(default);
    using var logger = LoggerFactory.Create(_ => { });
    var provider = new PersistenceContextProvider(logger, null);
    await provider.ApplyAllPendingUpdatesAsync();
    await new DataInitialization(provider, logger).CreateInitialDataAsync(1, false);
    using var context = provider.CreateNewContext();
    var game = (await context.GetAsync<GameConfiguration>()).Single();
    game.ExperienceRate = 100; game.MasterExperienceRate = 100;
    var server = (await context.GetAsync<GameServerDefinition>()).Single();
    server.Description = "Aether Ascension x100 — local staging"; server.ExperienceRate = 1;
    var system = (await context.GetAsync<SystemConfiguration>()).Single();
    system.AutoStart = true; system.AutoUpdateSchema = false; system.ReadConsoleInput = true;
    var reset = (await context.GetAsync<PlugInConfiguration>()).Single(p => p.TypeId == typeof(ResetFeaturePlugIn).GUID);
    var resetConfig = System.Text.Json.Nodes.JsonNode.Parse(reset.CustomConfiguration ?? "{}")!.AsObject();
    resetConfig["GrandResetEnabled"] = true; resetConfig["PrestigeEnabled"] = false;
    resetConfig["PrestigeRealm"] = "ascension"; resetConfig["PrestigeResetsRequired"] = 100;
    resetConfig["LevelAfterGrandReset"] = 1; resetConfig["PrestigeDeliveryEnabled"] = false;
    resetConfig["ResetStats"] = true; resetConfig["ReplacePointsPerReset"] = true;
    reset.CustomConfiguration = resetConfig.ToJsonString();
    // Rule configured, economy deliberately inactive pending separate acceptance and approval.
    if (Environment.GetEnvironmentVariable("AETHER_STAGE_PROFILE") == "first-playable")
    {
        var events = (await context.GetAsync<PlugInConfiguration>()).Single(p => p.TypeId == typeof(GameEventPublisherPlugIn).GUID);
        events.IsActive = true;
        events.CustomConfiguration = JsonSerializer.Serialize(new GameEventPublisherConfiguration
        {
            PublishInvasionEvents = true,
            PublishMiniGameEvents = false,
            PublishCastleSiegeEvents = false,
            PublishGlobalNotices = false,
            PublishExcellentItemDrops = false,
            PublishAncientItemDrops = false,
            PublishBossKills = false,
            PublishLevelMilestones = false,
            PublishChatMessages = false,
        });
        foreach (var connect in await context.GetAsync<ConnectServerDefinition>()) { connect.ClientListenerPort += 1000; }
        foreach (var endpoint in server.Endpoints) { endpoint.NetworkPort += 1000; }
        foreach (var chat in await context.GetAsync<ChatServerDefinition>())
        {
            foreach (var endpoint in chat.Endpoints) { endpoint.NetworkPort += 1000; }
        }
    }
    system.IpResolver = IpResolverType.Custom; system.IpResolverParameter = "127.0.0.1";
    if (!await context.SaveChangesAsync()) { throw new InvalidOperationException("Staging configuration save failed."); }
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        foreach (var file in new[] { "001-prestige.sql", "002-delivery.sql", "003-gr-app-100.sql", "005-build-presets.sql" })
        {
            await using var migration = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prestige", file)), connection, transaction);
            await migration.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
    await accounts.InitializeAsync();
    await using var marker = new NpgsqlCommand("CREATE SCHEMA aether_staging; CREATE TABLE aether_staging.instance(singleton boolean PRIMARY KEY CHECK(singleton),nonce text NOT NULL); INSERT INTO aether_staging.instance VALUES(true,@nonce)", connection);
    marker.Parameters.AddWithValue("nonce", AetherStagingState.Nonce(dsn));
    await marker.ExecuteNonQueryAsync();
    Console.WriteLine("CONFIGURED: Season 6 Ep3, EXP/master x100, one game server; no gameplay verification, no test accounts, no Prestige activation.");
    return;
}
if (args.SequenceEqual(new[] { "validate-state" }))
{
    Console.WriteLine(JsonSerializer.Serialize(await AetherStagingState.ReadAsync(dsn, true)));
    return;
}
await AetherStagingState.ReadAsync(dsn, false);
if (args.Length != 0) { throw new ArgumentException("Only initialize-empty or serve (no arguments)."); }
var token = Environment.GetEnvironmentVariable("AETHER_NATIVE_ACCOUNTS_TOKEN") ?? "";
if (token.Length < 32 || token.Any(c => c > 127)) { throw new InvalidOperationException("Worker token missing."); }
var readToken = Environment.GetEnvironmentVariable("AETHER_GAME_READER_TOKEN");
var auditToken = Environment.GetEnvironmentVariable("AETHER_GUARDIAN_MIRROR_TOKEN");
if (auditToken is { Length: 64 } && auditToken.All(char.IsAsciiHexDigit))
{
    await using var auditConnection = new NpgsqlConnection(dsn);
    await auditConnection.OpenAsync().ConfigureAwait(false);
    var migrationPath = Path.Combine(AppContext.BaseDirectory, "prestige", "004-live-audit.sql");
    await using var auditMigration = new NpgsqlCommand(await File.ReadAllTextAsync(migrationPath).ConfigureAwait(false), auditConnection);
    await auditMigration.ExecuteNonQueryAsync().ConfigureAwait(false);
}
else
{
    auditToken = null;
}

await new ConfigFileDatabaseConnectionStringProvider().InitializeAsync(default);
using var authLogger = LoggerFactory.Create(_ => { });
var authProvider = new PersistenceContextProvider(authLogger, null);
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Logging.ClearProviders(); // Do not log request bodies, credentials, DB errors or provisioning identities.
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, Environment.GetEnvironmentVariable("AETHER_STAGE_PROFILE") == "first-playable" ? 18863 : 17863); k.Limits.MaxRequestBodySize = 4096; });
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var presented = ctx.Request.Headers.Authorization.ToString();
    var expected = ctx.Request.Path == "/live-audit" ? auditToken : token;
    bool Matches(string? value) => value is not null && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + value)));
    if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None)
        || !(Matches(expected) || (ctx.Request.Path == "/profile" && readToken is { Length: 64 } && Matches(readToken))))
    { ctx.Response.StatusCode = 401; return; }
    try { await next(ctx); }
    catch (ArgumentException) { ctx.Response.StatusCode = 400; }
    catch (InvalidOperationException) { ctx.Response.StatusCode = 409; }
    catch (Exception exception) { await Console.Error.WriteLineAsync(exception.GetType().FullName + "\n" + exception.StackTrace).ConfigureAwait(false); ctx.Response.StatusCode = 503; }
});
app.MapPost("/live-audit", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
    var id = body.GetProperty("id").GetString();
    var kind = body.GetProperty("kind").GetString();
    var at = body.GetProperty("at").GetDateTimeOffset();
    if (id is not { Length: 64 } || !id.All(char.IsAsciiHexDigit)
        || kind is not ("invasion_started" or "invasion_ended" or "treasure_started" or "treasure_ended" or "treasure_reward"
            or "session" or "player_entered" or "security" or "error" or "lifecycle" or "gm_bot")
        || body.GetProperty("realm").GetString() != "ascension" || body.GetProperty("server").GetString() != "0")
    {
        return Results.BadRequest();
    }

    await using var auditConnection = new NpgsqlConnection(dsn);
    await auditConnection.OpenAsync(ctx.RequestAborted).ConfigureAwait(false);
    const string sql = """
        INSERT INTO aether_staging.live_event_audit(id, occurred_at, kind, payload)
        VALUES (@id, @at, @kind, CAST(@payload AS jsonb))
        ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id
        WHERE aether_staging.live_event_audit.payload = EXCLUDED.payload
        """;
    await using var command = new NpgsqlCommand(sql, auditConnection);
    command.Parameters.AddWithValue("id", id);
    command.Parameters.AddWithValue("at", at.UtcDateTime);
    command.Parameters.AddWithValue("kind", kind);
    command.Parameters.AddWithValue("payload", body.GetRawText());
    return await command.ExecuteNonQueryAsync(ctx.RequestAborted).ConfigureAwait(false) == 1
        ? Results.Json(new { status = "recorded" })
        : Results.Conflict();
});
app.MapGet("/health", async () => await AetherStagingState.ReadAsync(dsn, false));
app.MapPost("/snapshot", async () => await AetherStagingState.ReadAsync(dsn, true));
app.MapPost("/verify-login", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    using var configurationContext = authProvider.CreateNewContext();
    var game = (await configurationContext.GetAsync<GameConfiguration>()).Single();
    using var playerContext = authProvider.CreateNewPlayerContext(game);
    // Invoke the same native repository authentication used by OpenMU LoginAction.
    var state = await playerContext.AuthenticateAsync(body.GetProperty("login").GetString()!, body.GetProperty("password").GetString()!);
    if (state != MUnique.OpenMU.DataModel.Entities.AccountState.Normal) { ctx.Response.StatusCode = 401; return; }
    await ctx.Response.WriteAsJsonAsync(new { status = "NATIVE_AUTHENTICATION_PASS", protocol_gameplay = "MANUAL REQUIRED" });
});
app.MapPost("/shutdown", (HttpContext ctx) =>
{
    ctx.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
    return Results.Json(new { status = "stopping" });
});
app.MapPost("/create", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var owner = body.GetProperty("owner").GetInt64();
    var operation = body.GetProperty("operation").GetGuid();
    var login = body.GetProperty("login").GetString()!;
    var password = body.GetProperty("password").GetString()!;
    var id = await accounts.CreateAsync(owner, operation, login, password);
    await ctx.Response.WriteAsJsonAsync(new { account_id = id, login });
});
app.MapPost("/revoke", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    await accounts.RevokeAsync(body.GetProperty("owner").GetInt64(), body.GetProperty("account_id").GetGuid());
    await ctx.Response.WriteAsJsonAsync(new { status = "revoked" });
});
app.MapPost("/profile", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var profile = await accounts.ProfileAsync(body.GetProperty("owner").GetInt64(), body.GetProperty("account_id").GetGuid(),
        Stats.Level.Id, Stats.Resets.Id, Stats.GrandResets.Id);
    return profile is null ? Results.NotFound() : Results.Json(profile);
});
app.MapPost("/password", async (HttpContext ctx) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
    var changed = await accounts.ChangePasswordAsync(body.GetProperty("owner").GetInt64(), body.GetProperty("account_id").GetGuid(),
        body.GetProperty("password").GetString() ?? string.Empty);
    return changed ? Results.Json(new { status = "password_changed" }) : Results.Conflict();
});
await app.RunAsync();
