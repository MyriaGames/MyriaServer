using Microsoft.EntityFrameworkCore;
using Myria.Server.ClassicRealm.Models;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>Security review 2026-11-26, A06 F11: two concurrent debits must not both succeed.</summary>
public class GuildTreasuryConcurrencyTests
{
    [Fact]
    public async Task SecondConcurrentDebit_Fails_InsteadOfOverwritingTheFirst()
    {
        using var host = new RealmDbHost();
        var leader = new Character { UserId = "alice", Name = "Alicia" };
        host.Db.Characters.Add(leader);
        await host.Db.SaveChangesAsync();
        var guild = new Guild { Name = "Silver Hand", Tag = "SH", LeaderCharacterId = leader.Id };
        host.Db.Guilds.Add(guild);
        await host.Db.SaveChangesAsync();
        host.Db.GuildTreasuries.Add(new GuildTreasury { GuildId = guild.Id, Balance = 1000 });
        await host.Db.SaveChangesAsync();

        // Two requests read the same balance (both pass a "Balance >= 800" check) ...
        using var first = host.NewContext();
        using var second = host.NewContext();
        var t1 = await first.GuildTreasuries.SingleAsync(t => t.GuildId == guild.Id);
        var t2 = await second.GuildTreasuries.SingleAsync(t => t.GuildId == guild.Id);

        // ... and both debit.
        t1.Balance -= 800;
        await first.SaveChangesAsync();
        t2.Balance -= 800;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        using var check = host.NewContext();
        Assert.Equal(200, (await check.GuildTreasuries.SingleAsync(t => t.GuildId == guild.Id)).Balance);
    }
}
