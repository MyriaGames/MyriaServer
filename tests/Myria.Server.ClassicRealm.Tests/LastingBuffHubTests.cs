using Myria.Lib.Core.Services.Builder;
using Myria.Server.ClassicRealm.Repositories;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>
/// v0.3 lasting buffs: the Druid's Verdant Vigor can be cast outside a fight on yourself or anyone in
/// the same room (party or not); the buff is saved with the character.
/// </summary>
[Collection("RealmDb")]
public class LastingBuffHubTests
{
    public LastingBuffHubTests(RealmDbFixture _) { }

    private static async Task<(HubHarness realm, FakeCallerContext bob, FakeCallerContext alice)> DruidAndAllyAsync(int bobRoom = 1, int aliceRoom = 1, bool party = true)
    {
        var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        var alice = realm.Connect("alice");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.Hub(alice).LoadCharacter("Alicechar"));
        Assert.True(await realm.PlaceAsync(bob, bobRoom));
        Assert.True(await realm.PlaceAsync(alice, aliceRoom));

        var druid = realm.Session.Get(bob.ConnectionId)!;
        druid.Class = "Druid";
        SkillFactory.UpdateSkills(druid);
        druid.CurrentMana = druid.MaxMana;
        if (party)
        {
            var (partyId, _, _) = realm.Party.GetOrCreate("Bobchar");
            Assert.True(realm.Party.Join(partyId, "Alicechar").success);
        }
        return (realm, bob, alice);
    }

    [Fact]
    public async Task VerdantVigor_OnSomeoneHere_BuffsThem_AndIsSaved()
    {
        var (realm, bob, alice) = await DruidAndAllyAsync(party: false);
        using var _r = realm;
        var druid = realm.Session.Get(bob.ConnectionId)!;
        var ally = realm.Session.Get(alice.ConnectionId)!;
        int maxHp = ally.MaxHealth, mana = druid.CurrentMana;

        var result = await realm.Hub(bob).CastSkillOutOfCombat("verdant_vigor", "Alicechar");

        Assert.True(result.Success, result.Reason);
        Assert.True(ally.MaxHealth > maxHp);
        Assert.True(druid.CurrentMana < mana);
        Assert.Equal(druid.CurrentMana, result.CasterMana);

        using var db = realm.NewDb();
        var saved = await new SqlCharacterRepository(db).LoadAsync("alice", "Alicechar");
        Assert.Equal("verdant_vigor", Assert.Single(saved!.LastingBuffs).SkillId);
    }

    [Fact]
    public async Task VerdantVigor_WithoutATarget_BuffsTheCaster()
    {
        var (realm, bob, _) = await DruidAndAllyAsync(party: false);
        using var _r = realm;

        var result = await realm.Hub(bob).CastSkillOutOfCombat("verdant_vigor", null);

        Assert.True(result.Success, result.Reason);
        Assert.Single(realm.Session.Get(bob.ConnectionId)!.LastingBuffs);
    }

    [Fact]
    public async Task SomeoneInAnotherRoom_OrUnknown_IsRefused_EvenInMyParty()
    {
        var (realm, bob, alice) = await DruidAndAllyAsync(bobRoom: 1, aliceRoom: 2);
        using var _r = realm;
        Assert.Equal("skill.cast.noTarget", (await realm.Hub(bob).CastSkillOutOfCombat("verdant_vigor", "Alicechar")).Reason);
        Assert.Equal("skill.cast.noTarget", (await realm.Hub(bob).CastSkillOutOfCombat("verdant_vigor", "Nobody")).Reason);
        Assert.Empty(realm.Session.Get(alice.ConnectionId)!.LastingBuffs);
    }

    [Fact]
    public async Task OtherSkills_CannotBeCastOutsideAFight()
    {
        var (realm, bob, _) = await DruidAndAllyAsync();
        using var _r = realm;
        var result = await realm.Hub(bob).CastSkillOutOfCombat("natures_healing", null);
        Assert.False(result.Success);
        Assert.Equal("skill.cast.notOutOfCombat", result.Reason);
    }
}
