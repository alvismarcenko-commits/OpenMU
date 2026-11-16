// <copyright file="PlayerContext.CharacterExchange.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Threading;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// Transaction boundary for character exchanges. No schema changes are required.
/// </summary>
internal partial class PlayerContext : IPrestigeCharacterContext
{
    private bool _exchangeContextInvalid;

    /// <inheritdoc />
    public async ValueTask<(string GrandResets, string Balance)?> ReadPrestigeAsync(DataModel.Entities.Character character, string realm, CancellationToken cancellationToken = default)
    {
        this.EnsurePrestigeStagingDatabase();
        using var contextLock = await this.LockAsync(cancellationToken).ConfigureAwait(false);
        this.EnsureExchangeContextValid();
        await this.Context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = this.Context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT gr_count::text, balance::text
                FROM aether_prestige.characters
                WHERE realm=@realm AND character_id=@character AND migration_state='approved'
                """;
            Add(command, "realm", realm);
            Add(command, "character", ((Character)character).Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? (reader.GetString(0), reader.GetString(1))
                : null;
        }
        finally
        {
            await this.Context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> ExecutePrestigeExchangeAsync(DataModel.Entities.Character character, string realm, string operationId, int resetsBefore, CancellationToken cancellationToken = default)
    {
        this.EnsurePrestigeStagingDatabase();
        string result = string.Empty;
        await this.SaveCharacterExchangeCoreAsync(character, async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM aether_prestige.characters WHERE realm=@realm AND character_id=@character FOR UPDATE";
            Add(command, "realm", realm);
            Add(command, "character", ((Character)character).Id);
            var revision = await command.ExecuteScalarAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Prestige enrollment/migration approval required.");
            command.CommandText = "SELECT aether_prestige.complete_gr(@realm,@character,@operation,@revision,@resets,@hash)::text";
            Add(command, "operation", operationId);
            Add(command, "revision", Convert.ToInt64(revision));
            Add(command, "resets", resetsBefore);
            Add(command, "hash", $"native:{realm}:{((Character)character).Id}:{operationId}:{resetsBefore}");
            result = (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Missing prestige receipt."));
        }, cancellationToken, async (connection, transaction, token) =>
        {
            // Refuse a repeated native operation BEFORE saving any new tracked progress.
            // The SQL receipt alone would prevent a second credit but could otherwise allow
            // a replay to save an unrelated newer character snapshot as level/reset zero.
            await using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM aether_prestige.receipts WHERE realm=@realm AND character_id=@character AND operation_id=@operation";
            Add(check, "realm", realm); Add(check, "character", ((Character)character).Id); Add(check, "operation", operationId);
            if (await check.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            { throw new InvalidOperationException("Native GR operation already committed; reconnect and reload."); }
        }).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<string> DeliverPrestigeRewardAsync(DataModel.Entities.Character character, string realm, Guid purchaseId, Func<int, int, CancellationToken, ValueTask<DataModel.Entities.Item>> createItem, CancellationToken cancellationToken = default)
    {
        this.EnsurePrestigeStagingDatabase();
        string result = "pending";
        await this.SaveCharacterExchangeCoreAsync(character, null, cancellationToken, async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Add(command, "realm", realm); Add(command, "character", ((Character)character).Id); Add(command, "purchase", purchaseId);
            // Same lock order as GR/purchase/refund after native Character lock.
            command.CommandText = "SELECT character_id FROM aether_prestige.characters WHERE realm=@realm AND character_id=@character AND migration_state='approved' FOR UPDATE";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null) { throw new InvalidOperationException("Prestige enrollment is not approved."); }
            command.CommandText = "SELECT activated FROM aether_prestige.rules WHERE realm=@realm FOR SHARE";
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) { throw new InvalidOperationException("Prestige delivery disabled by realm rules."); }
            command.CommandText = "SELECT d.status,d.item_group,d.item_number,d.quantity FROM aether_prestige.delivery d JOIN aether_prestige.purchases p ON p.id=d.purchase_id AND p.realm=d.realm AND p.character_id=d.character_id WHERE d.purchase_id=@purchase AND d.realm=@realm AND d.character_id=@character FOR UPDATE OF d,p";
            string status; int group; int number; int quantity;
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) { throw new InvalidOperationException("No owned delivery."); }
                status = reader.GetString(0); group = reader.GetInt32(1); number = reader.GetInt32(2); quantity = reader.GetInt32(3);
            }
            if (status != "pending") { result = status; return; }
            if (character.Inventory is null) { throw new InvalidOperationException("Inventory missing."); }
            var storedInventory = await this.Context.Set<Character>().AsNoTracking().Where(c => c.Id == ((Character)character).Id)
                .SelectMany(c => c.RawInventory.RawItems).Select(i => new { i.Id, i.ItemSlot, i.Level, i.Durability }).ToListAsync(token).ConfigureAwait(false);
            var originals = character.Inventory!.Items.Cast<Item>().Where(i => this.Context.Entry(i).State != EntityState.Added)
                .Select(i => new { i.Id, ItemSlot = this.Context.Entry(i).Property(x => x.ItemSlot).OriginalValue, Level = this.Context.Entry(i).Property(x => x.Level).OriginalValue, Durability = this.Context.Entry(i).Property(x => x.Durability).OriginalValue }).ToList();
            if (!storedInventory.OrderBy(i => i.Id).SequenceEqual(originals.OrderBy(i => i.Id)))
            { throw new InvalidOperationException("Inventory snapshot is stale; reconnect before claim."); }
            // Verified pinned Season6 inheritance: a single basic Jewel of Chaos only.
            if (group != 12 || number != 15 || quantity != 1) { throw new InvalidOperationException("Unverified native reward."); }
            var item = await createItem(group, number, token).ConfigureAwait(false);
            if (item is not Item persistent || persistent.Id == Guid.Empty || this.Context.Entry(persistent).State != EntityState.Added
                || item.Level != 0 || item.HasSkill || item.SocketCount != 0 || item.ItemOptions.Count != 0 || item.ItemSetGroups.Count != 0
                || item.Definition is null || item.Definition.Group != group || item.Definition.Number != number || item.Definition.Width != 1 || item.Definition.Height != 1 || item.Durability != 1
                || character.Inventory is null || !character.Inventory.Items.Contains(item) || item.ItemSlot < 12)
            { throw new InvalidOperationException("Invalid native item or inventory placement."); }
            Add(command, "item", persistent.Id);
            command.CommandText = "UPDATE aether_prestige.delivery SET status='delivered',item_id=@item,delivered_at=extract(epoch from now())::bigint WHERE purchase_id=@purchase AND status='pending'; UPDATE aether_prestige.purchases SET status='delivered' WHERE id=@purchase AND status='pending'; INSERT INTO aether_prestige.audit VALUES(gen_random_uuid(),'native','delivery',@realm,@character,@purchase::text,extract(epoch from now())::bigint)";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            result = "delivered";
        }).ConfigureAwait(false);
        return result;
    }

    private void EnsurePrestigeStagingDatabase()
    {
        var name = this.Context.Database.GetDbConnection().Database;
        if (!new[] { "aether_prestige_staging_", "aether_prestige_test_", "aether_gr_test_", "aether_playable_ascension_" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
        { throw new InvalidOperationException("Prestige candidate refuses non-isolated database."); }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }


    /// <inheritdoc />
    public ValueTask SaveCharacterExchangeAsync(DataModel.Entities.Character character, CancellationToken cancellationToken = default)
        => this.SaveCharacterExchangeCoreAsync(character, null, cancellationToken);

    private async ValueTask SaveCharacterExchangeCoreAsync(DataModel.Entities.Character character, Func<DbConnection, DbTransaction, CancellationToken, ValueTask>? beforeCommit, CancellationToken cancellationToken, Func<DbConnection, DbTransaction, CancellationToken, ValueTask>? beforeSave = null)
    {
        using var contextLock = await this.LockAsync(cancellationToken).ConfigureAwait(false);
        this.EnsureExchangeContextValid();
        try
        {
            if (character is not Character persistentCharacter || this.Context.Entry(persistentCharacter).State != EntityState.Unchanged)
            {
                // DetectChanges can mark the character modified before this operation is called.
                if (character is not Character tracked || this.Context.Entry(tracked).State != EntityState.Modified)
                {
                    throw new InvalidOperationException("A persisted tracked character is required.");
                }

                persistentCharacter = tracked;
            }

            if (this.Context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                throw new NotSupportedException("Atomic character exchanges require PostgreSQL.");
            }

            await using var transaction = await this.Context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Every exchange locks the same persisted character, including across game processes.
            // Derive schema/table names from EF metadata; identifiers are never supplied by a player.
            var entityType = this.Context.Model.FindEntityType(typeof(Character))!;
            var schema = entityType.GetSchema()!.Replace("\"", "\"\"", StringComparison.Ordinal);
            var table = entityType.GetTableName()!.Replace("\"", "\"\"", StringComparison.Ordinal);
            var sql = $"SELECT \"Id\" FROM \"{schema}\".\"{table}\" WHERE \"Id\" = {{0}} FOR UPDATE";
            await this.Context.Database.ExecuteSqlRawAsync(sql, new object[] { persistentCharacter.Id }, cancellationToken).ConfigureAwait(false);

            var stored = await this.Context.Set<Character>().AsNoTracking()
                .Where(c => c.Id == persistentCharacter.Id)
                .Select(c => new { c.Experience, c.LevelUpPoints })
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var entry = this.Context.Entry(persistentCharacter);
            if (stored.Experience != entry.Property(c => c.Experience).OriginalValue
                || stored.LevelUpPoints != entry.Property(c => c.LevelUpPoints).OriginalValue)
            {
                throw new InvalidOperationException("Character progress is stale; reload before retrying.");
            }

            // Compare ALL stat originals, not the mutated in-memory values. Unsaved earned resets
            // remain valid, but a second context loaded before another exchange cannot overwrite it.
            foreach (var attribute in character.Attributes.Cast<StatAttribute>())
            {
                var trackedAttribute = this.Context.Entry(attribute);
                if (trackedAttribute.State == EntityState.Added)
                {
                    throw new InvalidOperationException("Character attributes must be persisted before an exchange.");
                }

                var storedValue = await this.Context.Set<StatAttribute>().AsNoTracking()
                    .Where(a => a.Id == attribute.Id).Select(a => a.Value)
                    .SingleAsync(cancellationToken).ConfigureAwait(false);
                if (storedValue != trackedAttribute.Property(a => a.Value).OriginalValue)
                {
                    throw new InvalidOperationException("Character attributes are stale; reload before retrying.");
                }
            }

            if (beforeSave is not null)
            {
                await beforeSave(this.Context.Database.GetDbConnection(), transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            }

            // Explicit transaction covers every tracked stat, character field and inventory change.
            // Accept only AFTER commit. Never retry a possibly committed operation automatically.
            await this.Context.SaveChangesAsync(false, cancellationToken).ConfigureAwait(false);
            if (beforeCommit is not null)
            {
                await beforeCommit(this.Context.Database.GetDbConnection(), transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            this.Context.ChangeTracker.AcceptAllChanges();
        }
        catch
        {
            // Includes lost commit acknowledgements: a later periodic save must not overwrite
            // committed progress with the action's restored in-memory snapshot.
            this._exchangeContextInvalid = true;
            throw;
        }
    }

    /// <inheritdoc />
    protected override void ValidateSave() => this.EnsureExchangeContextValid();

    private void EnsureExchangeContextValid()
    {
        if (this._exchangeContextInvalid)
        {
            throw new InvalidOperationException("Character exchange context is invalid; reconnect and reload persisted progress.");
        }
    }
}
