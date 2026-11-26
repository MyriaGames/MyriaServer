using Myria.Lib.Core.Systems.World;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>v0.3 villages through the hub: Selvra (room 99, west of Novmito road room 4) has an
/// Adventurers' Guild house that posts bounties for the overrun road nearby.</summary>
[Collection("RealmDb")]
public class VillageHubTests
{
    [Fact]
    public async Task AVillageGuildHouse_PostsBountiesForTheRoadNearby()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.PlaceAsync(bob, 4));
        Assert.True(await realm.Hub(bob).JoinRoom(99));         // walked in from the road

        Assert.True(await realm.Hub(bob).JoinAdventurersGuild());
        var world = realm.World.State!;
        while (world.PopulationGap(new PopulationKey(4, 33)) == 0) world.Tick();

        var offers = await realm.Hub(bob).GetDynamicQuestOffers(DynamicQuestRules.GuildNpcId);
        Assert.Contains(offers, o => o.Preview.RequiredRoomId == 4);
    }
}
