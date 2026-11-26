using Myria.Server.Realm.Models;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>Operator bans: account-level (realm-only) and per-character, with expiry.</summary>
public class BanTests
{
    private static async Task<RealmDbHost> HostWithCharacterAsync()
    {
        var host = new RealmDbHost();
        host.Db.Characters.Add(new Character { UserId = "alice", Name = "Alicia" });
        await host.Db.SaveChangesAsync();
        return host;
    }

    [Fact]
    public async Task AccountBan_IsActiveUntilItExpires()
    {
        using var host = new RealmDbHost();
        var bans = new BanService(host.Db);

        await bans.BanAccountAsync("alice", minutes: 60, reason: "spam");
        Assert.NotNull(await bans.GetActiveAccountBanAsync("alice"));
        Assert.Null(await bans.GetActiveAccountBanAsync("bob")); // other accounts untouched

        host.Db.AccountBans.Single().BannedUntil = DateTime.UtcNow.AddMinutes(-1);
        await host.Db.SaveChangesAsync();
        Assert.Null(await bans.GetActiveAccountBanAsync("alice"));
    }

    [Fact]
    public async Task AccountBan_WithNoDuration_IsPermanent_AndReBanReplacesTheRow()
    {
        using var host = new RealmDbHost();
        var bans = new BanService(host.Db);

        var perm = await bans.BanAccountAsync("alice", minutes: null, reason: null);
        await bans.BanAccountAsync("alice", minutes: 30, reason: "shortened");

        Assert.True(Bans.IsPermanent(perm.Until));
        var row = Assert.Single(host.Db.AccountBans);
        Assert.Equal("shortened", row.Reason);
        Assert.False(Bans.IsPermanent(row.BannedUntil));
    }

    [Fact]
    public async Task AccountBan_IsCaseSensitive_LikeAccountUsernames()
    {
        using var host = new RealmDbHost();
        var bans = new BanService(host.Db);

        await bans.BanAccountAsync("Alice", 60, null);

        Assert.Null(await bans.GetActiveAccountBanAsync("alice"));
    }

    [Fact]
    public async Task LiftAccountBan_RemovesIt_AndReportsWhetherOneExisted()
    {
        using var host = new RealmDbHost();
        var bans = new BanService(host.Db);
        await bans.BanAccountAsync("alice", 60, null);

        Assert.True(await bans.LiftAccountBanAsync("alice"));
        Assert.False(await bans.LiftAccountBanAsync("alice"));
        Assert.Null(await bans.GetActiveAccountBanAsync("alice"));
    }

    [Fact]
    public async Task CharacterBan_OnlyAffectsThatCharacterOfThatOwner()
    {
        using var host = await HostWithCharacterAsync();
        host.Db.Characters.Add(new Character { UserId = "alice", Name = "Second" });
        await host.Db.SaveChangesAsync();
        var bans = new BanService(host.Db);

        var result = await bans.BanCharacterAsync("Alicia", 60, "griefing");

        Assert.Equal("alice", result!.Value.Username);
        Assert.NotNull(await bans.GetActiveCharacterBanAsync("alice", "Alicia"));
        Assert.Null(await bans.GetActiveCharacterBanAsync("alice", "Second"));
        Assert.Null(await bans.GetActiveCharacterBanAsync("mallory", "Alicia")); // not their character
        Assert.Null(await bans.BanCharacterAsync("NoSuchChar", 60, null));
    }

    [Fact]
    public async Task LiftCharacterBan_ClearsBanAndReason()
    {
        using var host = await HostWithCharacterAsync();
        var bans = new BanService(host.Db);
        await bans.BanCharacterAsync("Alicia", 60, "griefing");

        Assert.Equal("alice", await bans.LiftCharacterBanAsync("Alicia"));

        var c = host.NewContext().Characters.Single();
        Assert.Null(c.BannedUntil);
        Assert.Null(c.BanReason);
    }

    [Fact]
    public async Task Reason_IsTrimmedAndCappedAt500()
    {
        using var host = new RealmDbHost();
        var bans = new BanService(host.Db);

        var ban = await bans.BanAccountAsync("alice", 60, "  " + new string('x', 900) + "  ");

        Assert.Equal(500, ban.Reason!.Length);
    }
}
