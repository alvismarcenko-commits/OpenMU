// <copyright file="MarketplaceGateway.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGateway;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherMarketplaceDelivery;
using AetherMarketplaceGameServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Private loopback bridge from existing website authentication to actual native GameServer actors.</summary>
public sealed class MarketplaceGateway
{
    private readonly Func<IEnumerable<IGameContext>> _contexts;
    private readonly MarketplaceGatewayOptions _options;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="MarketplaceGateway"/> class.</summary>
    /// <param name="contexts">The actual host-owned GameServer contexts.</param>
    /// <param name="options">The explicit trusted bridge configuration.</param>
    /// <param name="logger">The operator logger.</param>
    public MarketplaceGateway(Func<IEnumerable<IGameContext>> contexts, MarketplaceGatewayOptions options, ILogger logger)
    {
        this._contexts = contexts;
        this._options = options;
        this._logger = logger;
    }

    /// <summary>Maps the private bridge; no administrator operations are exposed.</summary>
    /// <param name="endpoints">The existing host endpoint builder.</param>
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/marketplace/{operation}", this.HandleAsync).AllowAnonymous();
    }

    /// <summary>Authenticates a backend request before resolving any player or database source.</summary>
    /// <param name="http">The HTTP context.</param>
    /// <param name="operation">The narrowly scoped operation.</param>
    /// <returns>The operation response.</returns>
    public async Task<IResult> HandleAsync(HttpContext http, string operation)
    {
        if (!this._options.Enabled)
        {
            return Results.StatusCode(503);
        }

        if (http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
            || this._options.Secret.Length < 32 || !http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(http.Request.Headers.Authorization.ToString()[7..]), Encoding.UTF8.GetBytes(this._options.Secret)))
        {
            return Results.Unauthorized();
        }

        if (http.Request.ContentLength > 8192)
        {
            return Results.StatusCode(413);
        }

        try
        {
            var request = await http.Request.ReadFromJsonAsync<MarketplaceCommand>(http.RequestAborted).ConfigureAwait(false);
            if (request is null || request.WebsiteAccountId <= 0 || request.Realm.Length > 32
                || request.Search?.Length > 100 || operation is not ("browse" or "characters" or "inventory" or "history" or "list" or "buy" or "cancel" or "retry"))
            {
                return Results.BadRequest(new { code = "INVALID_REQUEST" });
            }

            var routes = this._options.Routing.Realms.Where(r => string.Equals(r.Value, request.Realm, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (routes.Length == 0)
            {
                return Results.BadRequest(new { code = "INVALID_REALM" });
            }

            var realm = routes[0].Value;
            var contexts = this._contexts().Where(c => c is IGameServerContext s && routes.Any(r => r.Key == s.Id)).ToArray();
            if (contexts.Length == 0 || !this._options.Routing.ConnectionVariables.TryGetValue(realm, out var variable)
                || Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } connection)
            {
                return Results.Json(new { code = "REALM_OFFLINE" }, statusCode: 503);
            }

            await using var source = NpgsqlDataSource.Create(connection);
            var token = http.RequestAborted;
            await using (var storeCommand = source.CreateCommand("SELECT store_id FROM aether_marketplace.native_delivery_store WHERE singleton"))
            {
                using var nativeContext = contexts[0].PersistenceContextProvider.CreateNewPlayerContext(contexts[0].Configuration);
                if (await storeCommand.ExecuteScalarAsync(token).ConfigureAwait(false) is not Guid store
                    || !await nativeContext.VerifyMarketplaceDeliveryStoreAsync(store, token).ConfigureAwait(false))
                {
                    return Results.Json(new { code = "STORE_MISMATCH" }, statusCode: 503);
                }
            }

            if (operation == "browse")
            {
                if (request.Currency is not null && request.Currency is not ("Zen" or "AetherCoins"))
                {
                    return Results.BadRequest(new { code = "INVALID_CURRENCY" });
                }

                return Results.Ok(new { items = await BrowseAsync(source, contexts[0], request, realm, token).ConfigureAwait(false) });
            }

            if (operation == "characters")
            {
                await using var command = source.CreateCommand("""
                    SELECT b.character_id,ch."Name" FROM aether_marketplace.native_account_binding b
                    JOIN data."Character" ch ON ch."Id"=b.character_id AND ch."AccountId"=b.openmu_account_id
                    WHERE b.realm=@realm AND b.marketplace_account_id=@owner ORDER BY ch."Name"
                    """);
                command.Parameters.AddWithValue("realm", realm);
                command.Parameters.AddWithValue("owner", request.WebsiteAccountId);
                var characters = new List<object>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    characters.Add(new { id = reader.GetGuid(0), name = reader.GetString(1) });
                }

                return Results.Ok(new { items = characters });
            }

            if (operation == "history")
            {
                return Results.Ok(new { items = await HistoryAsync(source, request, realm, token).ConfigureAwait(false) });
            }

            await using var binding = source.CreateCommand("SELECT openmu_account_id FROM aether_marketplace.native_account_binding WHERE realm=@realm AND character_id=@character AND marketplace_account_id=@owner");
            binding.Parameters.AddWithValue("realm", realm);
            binding.Parameters.AddWithValue("character", request.CharacterId);
            binding.Parameters.AddWithValue("owner", request.WebsiteAccountId);
            if (await binding.ExecuteScalarAsync(token).ConfigureAwait(false) is not Guid accountId)
            {
                return Results.Json(new { code = "UNAUTHORIZED" }, statusCode: 403);
            }

            var matches = new List<(IGameContext Context, Player Player)>();
            foreach (var context in contexts)
            {
                matches.AddRange((await context.GetPlayersAsync().ConfigureAwait(false))
                    .Where(p => p.IsConnected && p.SelectedCharacter?.Id == request.CharacterId && p.Account is IIdentifiable a && a.Id == accountId)
                    .Select(p => (context, p)));
            }

            if (operation == "cancel")
            {
                await using var command = source.CreateCommand("SELECT aether_marketplace.cancel_native_listing_v30(@id,@realm,@seller,@account)");
                command.Parameters.AddWithValue("id", request.ListingId);
                command.Parameters.AddWithValue("realm", realm);
                command.Parameters.AddWithValue("seller", request.CharacterId);
                command.Parameters.AddWithValue("account", accountId);
                var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                if (result is "CANCELLED" or "ALREADY_CANCELLED")
                {
                    foreach (var context in contexts)
                    {
                        await new GameServerNativeReturns(source, context, realm).ResumeAsync(request.ListingId, token).ConfigureAwait(false);
                    }
                }

                return Outcome(result);
            }

            if (matches.Count != 1 || matches[0].Player.PlayerState.CurrentState != PlayerState.EnteredWorld)
            {
                return Results.Json(new { code = "GAME_LOGIN_REQUIRED" }, statusCode: 409);
            }

            var actor = matches[0].Player;
            var owningContext = matches[0].Context;
            if (operation == "inventory")
            {
                return await actor.RunPersistenceExclusiveAsync(
                    () => ValueTask.FromResult<IResult>(Results.Ok(new
                    {
                        items = actor.Inventory?.Items.Select(i => new
                        {
                            id = ((IIdentifiable)i).Id,
                            name = i.Definition?.Name.ToString(),
                            i.Level,
                            i.Durability,
                            i.HasSkill,
                            i.SocketCount,
                            i.PetExperience,
                            options = i.ItemOptions.Select(o => new { name = o.ItemOption?.OptionType?.Name.ToString(), o.Level, o.Index }),
                            sets = i.ItemSetGroups.Select(s => new { s.AncientSetDiscriminator, name = s.ItemSetGroup?.Name.ToString() }),
                        }).ToArray(),
                    })),
                    token).ConfigureAwait(false);
            }

            if (operation == "list")
            {
                if (request.OperationId == Guid.Empty || !long.TryParse(request.Price, NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                    || price <= 0 || !Enum.TryParse<PlayerMarketCurrency>(request.Currency, out var currency) || !Enum.IsDefined(currency))
                {
                    return Results.BadRequest(new { code = "INVALID_PRICE_OR_CURRENCY" });
                }

                var listed = await new GameServerNativeCustody(source, realm).ListAsync(actor, request.OperationId, request.ItemId, price, currency, token).ConfigureAwait(false);
                return listed ? Results.Ok(new { code = "LISTED", listingId = request.OperationId }) : Results.Conflict(new { code = "CUSTODY_PENDING_OR_REJECTED" });
            }

            if (request.OperationId == Guid.Empty)
            {
                return Results.BadRequest(new { code = "INVALID_OPERATION_ID" });
            }

            string? resultCode = null;
            if (operation == "buy")
            {
                await using var reserve = source.CreateCommand("SELECT aether_marketplace.reserve_authorized_purchase_v30(@purchase,@listing,@buyer,@account,@realm)");
                reserve.Parameters.AddWithValue("purchase", request.OperationId);
                reserve.Parameters.AddWithValue("listing", request.ListingId);
                reserve.Parameters.AddWithValue("buyer", request.CharacterId);
                reserve.Parameters.AddWithValue("account", accountId);
                reserve.Parameters.AddWithValue("realm", realm);
                resultCode = await reserve.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                if (resultCode is not ("RESERVED" or "ALREADY_RESERVED"))
                {
                    return Outcome(resultCode);
                }
            }

            await using var purchase = source.CreateCommand("""
                SELECT p.currency,l.item_instance_id,p.status FROM aether_marketplace.purchase_reservation p
                JOIN aether_marketplace.player_listing l ON l.listing_id=p.listing_id
                WHERE p.purchase_id=@id AND p.realm=@realm AND p.buyer_character_id=@buyer
                """);
            purchase.Parameters.AddWithValue("id", request.OperationId);
            purchase.Parameters.AddWithValue("realm", realm);
            purchase.Parameters.AddWithValue("buyer", request.CharacterId);
            string purchaseCurrency;
            Guid itemId;
            await using (var reader = await purchase.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return Results.NotFound();
                }

                purchaseCurrency = reader.GetString(0);
                itemId = reader.GetGuid(1);
            }

            if (operation == "buy")
            {
                resultCode = purchaseCurrency == "Zen"
                    ? await new PostgreSqlAuthorizedZenSettlement(source, owningContext, realm, contexts).SettleAsync(actor, request.OperationId, token).ConfigureAwait(false)
                    : await new PostgreSqlAuthorizedAcSettlement(source, realm, true).SettleAsync(actor, request.OperationId, token).ConfigureAwait(false);
                if (resultCode is not ("SETTLED" or "ALREADY_SETTLED"))
                {
                    return Outcome(resultCode);
                }
            }

            var native = new GameServerCustodyDelivery(source, owningContext, realm);
            var delivery = await new SafeDeliveryCoordinator(native, native).DeliverAsync(request.OperationId, request.CharacterId, itemId.ToString("D"), token).ConfigureAwait(false);
            return Results.Ok(new { code = delivery is SafeDeliveryStatus.Delivered or SafeDeliveryStatus.AlreadyDelivered ? "COMPLETED" : "DELIVERY_PENDING", purchaseId = request.OperationId });
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Private Marketplace operation {Operation} failed.", operation);
            return Results.Json(new { code = "SERVICE_UNAVAILABLE" }, statusCode: 503);
        }
    }

    private static IResult Outcome(string? code) => Results.Json(new { code = code ?? "SERVICE_UNAVAILABLE" }, statusCode: code is "CANCELLED" or "ALREADY_CANCELLED" ? 200 : 409);

    private static async Task<List<object>> BrowseAsync(NpgsqlDataSource source, IGameContext context, MarketplaceCommand request, string realm, System.Threading.CancellationToken token)
    {
        var definitions = context.Configuration.Items.Where(d => string.IsNullOrWhiteSpace(request.Search)
            || (d.Name.ToString() ?? string.Empty).Contains(request.Search, StringComparison.OrdinalIgnoreCase)).Cast<IIdentifiable>().Select(d => d.Id).ToArray();

        await using var command = source.CreateCommand("""
            SELECT l.listing_id,l.currency,l.price,(to_jsonb(i)-'ItemStorageId')::text,
             COALESCE((SELECT jsonb_agg(to_jsonb(o)) FROM data."ItemOptionLink" o WHERE o."ItemId"=i."Id"),'[]')::text,
             COALESCE((SELECT jsonb_agg(to_jsonb(s)) FROM data."ItemItemOfItemSet" s WHERE s."ItemId"=i."Id"),'[]')::text
            FROM aether_marketplace.player_listing l JOIN data."Item" i ON i."Id"=l.item_instance_id
            JOIN aether_marketplace.native_item_custody c ON c.listing_id=l.listing_id AND c.status='HELD' AND c.escrow_storage_id=i."ItemStorageId"
            WHERE l.realm=@realm AND l.status='ACTIVE' AND (@currency IS NULL OR l.currency=@currency)
             AND i."DefinitionId"=ANY(@definitions)
            ORDER BY l.created_at DESC LIMIT 100
            """);
        command.Parameters.AddWithValue("realm", realm);
        command.Parameters.AddWithValue("definitions", definitions);
        command.Parameters.AddWithValue("currency", NpgsqlTypes.NpgsqlDbType.Text, (object?)request.Currency ?? DBNull.Value);
        var results = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3));
            var definition = item.GetProperty("DefinitionId").GetGuid();
            var nativeDefinition = context.Configuration.Items.FirstOrDefault(d => d is IIdentifiable id && id.Id == definition);
            var name = nativeDefinition?.Name.ToString() ?? definition.ToString();
            if (!string.IsNullOrWhiteSpace(request.Search) && !name.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new
            {
                id = reader.GetGuid(0),
                currency = reader.GetString(1),
                price = reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                name,
                item,
                options = JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)).EnumerateArray().Select(o => new
                {
                    name = context.Configuration.ItemOptions.Concat(nativeDefinition?.PossibleItemOptions ?? [])
                        .SelectMany(d => d.PossibleOptions).FirstOrDefault(d => d is IIdentifiable id && id.Id == o.GetProperty("ItemOptionId").GetGuid())?.OptionType?.Name.ToString(),
                    level = o.GetProperty("Level").GetInt32(),
                    index = o.GetProperty("Index").GetInt32(),
                }).ToArray(),
                sets = JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)).EnumerateArray().Select(s =>
                {
                    var nativeSet = context.Configuration.ItemSetGroups.SelectMany(g => g.Items)
                        .FirstOrDefault(g => g is IIdentifiable id && id.Id == s.GetProperty("ItemOfItemSetId").GetGuid());
                    return new { name = nativeSet?.ItemSetGroup?.Name.ToString(), ancientSetDiscriminator = nativeSet?.AncientSetDiscriminator };
                }).ToArray(),
            });
        }

        return results;
    }

    private static async Task<List<object>> HistoryAsync(NpgsqlDataSource source, MarketplaceCommand request, string realm, System.Threading.CancellationToken token)
    {
        await using var command = source.CreateCommand("""
            SELECT l.listing_id,l.currency,l.price,l.status,p.purchase_id,p.status,p.buyer_character_id,l.seller_character_id,r.status
            FROM aether_marketplace.player_listing l
            LEFT JOIN aether_marketplace.purchase_reservation p ON p.listing_id=l.listing_id
            LEFT JOIN aether_marketplace.native_listing_return r ON r.listing_id=l.listing_id
            WHERE l.realm=@realm AND EXISTS(SELECT 1 FROM aether_marketplace.native_account_binding b
             WHERE b.realm=l.realm AND b.marketplace_account_id=@owner AND b.character_id IN(l.seller_character_id,p.buyer_character_id))
            ORDER BY l.created_at DESC LIMIT 200
            """);
        command.Parameters.AddWithValue("realm", realm);
        command.Parameters.AddWithValue("owner", request.WebsiteAccountId);
        var results = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            results.Add(new
            {
                listingId = reader.GetGuid(0),
                currency = reader.GetString(1),
                price = reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                listingStatus = reader.GetString(3),
                purchaseId = await reader.IsDBNullAsync(4, token).ConfigureAwait(false) ? (Guid?)null : reader.GetGuid(4),
                purchaseStatus = await reader.IsDBNullAsync(5, token).ConfigureAwait(false) ? null : reader.GetString(5),
                buyerId = await reader.IsDBNullAsync(6, token).ConfigureAwait(false) ? (Guid?)null : reader.GetGuid(6),
                sellerId = reader.GetGuid(7),
                returnStatus = await reader.IsDBNullAsync(8, token).ConfigureAwait(false) ? null : reader.GetString(8),
            });
        }

        return results;
    }
}
