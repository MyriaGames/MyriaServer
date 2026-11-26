using Microsoft.Data.Sqlite;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>
/// Security review 2026-11-26, A03 F3: the native SQLite shipped with the server must include the fix for
/// CVE-2025-6965 (SQLite 3.50.2). EF Core 10 alone still pulls SQLitePCLRaw 2.1.x with an older SQLite.
/// </summary>
public class SqliteVersionTests
{
    [Fact]
    public void NativeSqlite_IsAtLeast_3_50_2()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"native SQLite is {version}");
    }
}
