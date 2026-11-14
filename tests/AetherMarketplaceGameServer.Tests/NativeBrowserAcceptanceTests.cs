// <copyright file="NativeBrowserAcceptanceTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer.Tests;

public sealed partial class NativeCustodyDeliveryTests
{
    [Test]
    [Explicit("Requires the isolated PostgreSQL runner, portal sources and browser runtime.")]
    public async Task BrowserAcceptanceWorkerAsync()
    {
        Assert.That(Environment.GetEnvironmentVariable("AETHER_V30_BROWSER_ACCEPTANCE"), Is.EqualTo("1"));
        await this.OriginalCustodyEntityAndReceiptSurviveFreshContextAsync("api-browser").ConfigureAwait(false);
        await using var verify = this._database.CreateCommand("""
            SELECT p.status, s.status, z.gross_amount, z.buyer_after, z.seller_after,
              (SELECT count(*) FROM data."Item" i WHERE i."Id"=l.item_instance_id AND i."ItemStorageId"=ch."InventoryId")
            FROM aether_marketplace.purchase_reservation p
            JOIN aether_marketplace.player_listing l ON l.listing_id=p.listing_id
            JOIN aether_marketplace.settlement_ledger s ON s.purchase_id=p.purchase_id
            JOIN aether_marketplace.native_zen_settlement z ON z.purchase_id=p.purchase_id
            JOIN data."Character" ch ON ch."Id"=p.buyer_character_id
            """);
        await using var reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.True);
        Assert.That(reader.GetString(0), Is.EqualTo("COMPLETED"));
        Assert.That(reader.GetString(1), Is.EqualTo("COMMITTED"));
        Assert.That(reader.GetInt64(2), Is.EqualTo(1000));
        Assert.That(reader.GetInt32(3), Is.EqualTo(1000));
        Assert.That(reader.GetInt32(4), Is.EqualTo(1000));
        Assert.That(reader.GetInt64(5), Is.EqualTo(1));
        Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.False);
    }

    private static async Task RunBrowserAsync(Uri gateway, string secret)
    {
        var root = Environment.GetEnvironmentVariable("AETHER_V30_PORTAL_ROOT") ?? throw new InvalidOperationException("Portal source required.");
        var python = Environment.GetEnvironmentVariable("AETHER_V30_TEST_PYTHON") ?? throw new InvalidOperationException("Python runtime required.");
        var start = new System.Diagnostics.ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "backend", "tests", "browser_acceptance.py"));
        start.Environment["AETHER_NATIVE_MARKETPLACE_ENABLED"] = "true";
        start.Environment["AETHER_NATIVE_MARKETPLACE_URL"] = gateway.ToString();
        start.Environment["AETHER_NATIVE_MARKETPLACE_TOKEN"] = secret;
        start.Environment["AETHER_V30_PROCESS_PROBE"] = "1";
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Browser runner failed to start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
        Assert.That(process.ExitCode, Is.Zero, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
