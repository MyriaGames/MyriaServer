using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems.World;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// v0.3 quest rework, part 1, through the real hub and game data: room populations gate which
/// monsters can be fought, shop purchases drain NPC stock, sales refill it, and the state survives
/// a save/load. Room 1 (Royas) has smith_default and trader_default; room 2 is an overworld room
/// with monsters 33-36.
/// </summary>
[Collection("RealmDb")]
public class WorldHubTests
{
    private static async Task<(HubHarness Realm, FakeCallerContext Conn, Character Player)> BobInRoomAsync(int roomId)
    {
        var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.Hub(bob).JoinRoom(roomId));
        return (realm, bob, realm.Session.Get(bob.ConnectionId)!);
    }

    private static void Exhaust(WorldState world, int roomId, int monsterId)
    {
        for (int i = 0; i < WorldRules.PopulationCap; i++) world.RecordKill(roomId, monsterId);
    }

    [Fact]
    public async Task FightsOnlyDrawMonsterTypesTheRoomStillHas()
    {
        var (realm, bob, _) = await BobInRoomAsync(2);
        using var _r = realm;
        foreach (var id in new[] { 33, 34, 35 }) Exhaust(realm.World.State!, 2, id);

        var start = await realm.Hub(bob).StartGroupCombat(2, soloOnly: false);

        Assert.True(start.Success, start.Reason);
        Assert.All(start.Monsters, m => Assert.Equal(MonsterService.GetMonsterById(36)!.Name, m.Name));
    }

    [Fact]
    public async Task ARoomWithNoPopulationLeft_HasNothingToFight()
    {
        var (realm, bob, _) = await BobInRoomAsync(2);
        using var _r = realm;
        foreach (var id in new[] { 33, 34, 35, 36 }) Exhaust(realm.World.State!, 2, id);

        var group = await realm.Hub(bob).StartGroupCombat(2, soloOnly: true);
        var solo  = await realm.Hub(bob).StartCombat(2);

        Assert.Equal("no_monsters", group.Reason);
        Assert.Equal("no_monsters", solo.Reason);
    }

    [Fact]
    public async Task BuyingFromAnNpc_DrainsItsStockOfTheItemAndItsIngredients()
    {
        var (realm, bob, player) = await BobInRoomAsync(1);
        using var _r = realm;
        Assert.True(player.Money.TryAdd(ItemFactory.CreateItem("iron_sword")!.BuyPrice));
        var world = realm.World.State!;
        var sword = new StockKey(1, "smith_default", "iron_sword");
        var ingot = new StockKey(1, "smith_default", "iron_ingot");
        int swordBefore = world.GetStock(sword), ingotBefore = world.GetStock(ingot);
        int ingotsPerSword = CraftingService.GetRecipe("smith_default", "iron_sword")!.Ingredients.Single(i => i.ItemId == "iron_ingot").Amount;

        var result = await realm.Hub(bob).BuyFromNpcShop("smith_default", "iron_sword", 1);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(swordBefore - 1, world.GetStock(sword));
        Assert.Equal(ingotBefore - ingotsPerSword, world.GetStock(ingot));
    }

    [Fact]
    public async Task SellingToAnNpc_RefillsTheNeediestNpcHereThatKeepsTheItem()
    {
        var (realm, bob, player) = await BobInRoomAsync(1);
        using var _r = realm;
        var world = realm.World.State!;
        var smithOre = new StockKey(1, "smith_default", "iron_ore");
        while (world.GetStock(smithOre) > 2) world.Tick(); // passive drain makes the smith the neediest
        int before = world.GetStock(smithOre);
        player.Inventory.AddItem(ItemFactory.CreateItem("iron_ore")!.Also(i => i.StackSize = 3), player);

        var result = await realm.Hub(bob).SellItemToNpc("iron_ore", 3);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(before + 3, world.GetStock(smithOre));
    }

    [Fact]
    public async Task WorldState_SurvivesASaveAndReload()
    {
        var (realm, _, _) = await BobInRoomAsync(1);
        using var _r = realm;
        realm.World.State!.RecordKill(2, 33);
        realm.World.State.RecordPurchase(1, "smith_default", "iron_sword", 4, Array.Empty<(string, int)>());
        await realm.World.SaveAsync();

        var reloaded = new WorldStateService(realm.ScopeFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldStateService>.Instance);
        await reloaded.LoadAsync();

        Assert.Equal(WorldRules.PopulationStart - 1, reloaded.State!.GetPopulation(new PopulationKey(2, 33)));
        Assert.Equal(WorldRules.StockTarget - 4, reloaded.State.GetStock(new StockKey(1, "smith_default", "iron_sword")));
    }
}

internal static class TestObjectExtensions
{
    public static T Also<T>(this T obj, Action<T> configure) { configure(obj); return obj; }
}
