using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Services;
using Myria.Server.ClassicRealm.Data;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>
/// Loads the real Data/common/*.json content once for the whole test run via
/// GameService.InitializeGame(). Everything that reconstructs a Character resolves item, quest and
/// room ids against those static registries, so without this a round-trip silently drops every
/// item it can't look up. Myria.Lib.Core's loaders are static, process-wide state, so this must
/// run exactly once and be shared — never re-initialize per test class.
/// </summary>
public sealed class RealmDbFixture
{
    public RealmDbFixture() => GameService.InitializeGame();
}

/// <summary>
/// Apply <c>[Collection("RealmDb")]</c> to any test class that loads game content or exercises the
/// character repository.
/// </summary>
[CollectionDefinition("RealmDb")]
public sealed class RealmDbCollection : ICollectionFixture<RealmDbFixture>
{
}

/// <summary>
/// A private realm database per test.
///
/// Real SQLite rather than EF's InMemory provider, deliberately: this schema leans on relational
/// behaviour InMemory fakes or ignores — the global unique index on Character.Name, its NOCASE
/// collation, the cascade deletes from Character to its sixteen child tables, and the Restrict
/// behaviour on friendships and blocks. Testing against InMemory would let bugs in exactly those
/// constraints through. The connection is held open for the host's lifetime because a
/// "DataSource=:memory:" database is destroyed the moment its last connection closes.
/// </summary>
internal sealed class RealmDbHost : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Db { get; }

    public RealmDbHost()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Db = NewContext();
        Db.Database.EnsureCreated();
    }

    /// <summary>
    /// A second context over the same database. Use this to read back what a write actually
    /// persisted: reading through the same context can be answered from its change tracker, which
    /// would hide a mapping that never reached SQL at all.
    /// </summary>
    public AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
