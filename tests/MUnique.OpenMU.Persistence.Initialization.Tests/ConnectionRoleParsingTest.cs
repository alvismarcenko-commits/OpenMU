// <copyright file="ConnectionRoleParsingTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Threading;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;

/// <summary>Connection aliases must resolve before native migrations generate role SQL.</summary>
[TestFixture]
[NonParallelizable]
public class ConnectionRoleParsingTest
{
    /// <summary>Tests supported PostgreSQL connection-string aliases and trailing fields.</summary>
    /// <param name="connectionString">The connection string.</param>
    [TestCase("Host=127.0.0.1;Username=aether_test;Password=test_secret")]
    [TestCase("Host=127.0.0.1;User Id=aether_test;Password=test_secret;")]
    [TestCase("host=127.0.0.1;user name=aether_test;password=test_secret")]
    [TestCase("Password=test_secret;Host=127.0.0.1;Username=aether_test")]
    public void ParsesRoleAndPassword(string connectionString)
    {
        using var provider = ConnectionConfigurator.UseTemporarily(new Settings(connectionString));
        foreach (var role in Enum.GetValues<DatabaseRole>())
        {
            Assert.That(ConnectionConfigurator.GetRoleName(role), Is.EqualTo("aether_test"));
            Assert.That(ConnectionConfigurator.GetRolePassword(role), Is.EqualTo("test_secret"));
        }
    }

    /// <summary>Implicit operating-system users must not produce blank CREATE ROLE SQL.</summary>
    [Test]
    public void RejectsMissingRole()
    {
        using var provider = ConnectionConfigurator.UseTemporarily(new Settings("Host=127.0.0.1;Password=test_secret"));
        Assert.Throws<InvalidOperationException>(() => ConnectionConfigurator.GetRoleName(DatabaseRole.Admin));
    }

    /// <summary>Runs all native migrations against an explicitly supplied disposable test database.</summary>
    [Test]
    public async Task MigratesDisposablePostgreSqlAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("AETHER_ROLE_MIGRATION_TEST_DSN");
        if (string.IsNullOrEmpty(connectionString))
        {
            Assert.Ignore("Disposable PostgreSQL fixture not supplied.");
            return;
        }

        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.That(parsed.Host, Is.EqualTo("127.0.0.1"));
        Assert.That(parsed.Database, Is.EqualTo("aether_role_parser_test"));
        using var provider = ConnectionConfigurator.UseTemporarily(new Settings(connectionString));
        using var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var persistence = new PersistenceContextProvider(logger, null);
        await persistence.ApplyAllPendingUpdatesAsync().ConfigureAwait(false);
        Assert.That(await persistence.IsDatabaseUpToDateAsync().ConfigureAwait(false), Is.True);
    }

    private sealed class Settings(string connectionString) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => null;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext
            => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType)
            => new() { ContextTypeName = contextType.FullName, ConnectionString = connectionString, DatabaseEngine = DatabaseEngine.Npgsql };
    }
}
