using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Server.Realm.Hubs;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Security review 2026-11-26, A06: the economy and game-rule fixes, exercised through the real hub
/// methods with the real game data (room 1 has healer, trader and smith; room 2 has none;
/// "co1_first_day" is a talk-only quest given by the smith).
/// </summary>
[Collection("RealmDb")]
public class EconomyIntegrityHubTests
{
    private static async Task<(HubHarness Realm, FakeCallerContext Conn, Myria.Lib.Core.Entities.Characters.Character Player)>
        BobInRoomAsync(int roomId)
    {
        var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.Hub(bob).JoinRoom(roomId));
        return (realm, bob, realm.Session.Get(bob.ConnectionId)!);
    }

    // ── F3 craft overflow ────────────────────────────────────────────────

    [Fact]
    public async Task Craft_QuantityThatWouldOverflow_IsRejectedAsInvalidQuantity()
    {
        var (realm, bob, _) = await BobInRoomAsync(1);
        using var _r = realm;

        var result = await realm.Hub(bob).Craft("smith_default", "anything", 1_073_741_824);

        Assert.False(result.Success);
        Assert.Equal("invalid_quantity", result.Reason);
    }

    // ── F4 NPC purchase with a full inventory ────────────────────────────

    [Fact]
    public async Task BuyFromNpc_PartlyFitting_AddsNothing_AndChargesNothing()
    {
        var (realm, bob, player) = await BobInRoomAsync(1);
        using var _r = realm;
        // A stackable item the trader really sells in this room
        Assert.True(Myria.Lib.Core.Services.NpcService.TryGet("trader_default", out var trader) && trader is not null);
        Item? potion = null;
        foreach (var id in Myria.Lib.Core.Services.NpcService.GetEffectiveShopItemNames(trader!, player.CurrentRoom))
            if (ItemFactory.TryCreateItem(id, out var candidate) && candidate is not null && candidate is not EquipmentItem && candidate.MaxStackSize > 1)
            { potion = candidate; break; }
        Assert.NotNull(potion);
        potion!.StackSize = 1;
        player.Inventory.Items.Clear();
        player.Inventory.Items.Add(potion);
        for (int i = 1; player.Inventory.Items.Count < player.Inventory.Capacity; i++)
            player.Inventory.Items.Add(new MaterialItem { Id = "filler_" + i, Name = "Filler", StackSize = 1 });
        player.Money.TryAdd(100_000);
        long moneyBefore = player.Money.Balance.BronzeTotal;

        var result = await realm.Hub(bob).BuyFromNpcShop("trader_default", potion.Id, potion.MaxStackSize);

        Assert.False(result.Success);
        Assert.Equal("inventory_full", result.Reason);
        Assert.Equal(1, potion.StackSize);
        Assert.Equal(moneyBefore, player.Money.Balance.BronzeTotal);
    }

    // ── F5 quests ────────────────────────────────────────────────────────

    [Fact]
    public async Task CompletedQuest_CannotBeAcceptedAgain()
    {
        var (realm, bob, player) = await BobInRoomAsync(1);
        using var _r = realm;

        Assert.True(await realm.Hub(bob).QuestAction("co1_first_day", false));
        Assert.True(await realm.Hub(bob).QuestAction("co1_first_day", true));
        long xpAfterFirst = player.Experience;

        Assert.False(await realm.Hub(bob).QuestAction("co1_first_day", false));
        Assert.False(await realm.Hub(bob).QuestAction("co1_first_day", true));
        Assert.Equal(xpAfterFirst, player.Experience);
    }

    [Fact]
    public async Task Quest_CanOnlyBeAcceptedWhereTheGiverIs()
    {
        var (realm, bob, _) = await BobInRoomAsync(2);
        using var _r = realm;
        Assert.False(await realm.Hub(bob).QuestAction("co1_first_day", false));
    }

    // ── F8 heal ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Heal_OnlyWhereAHealerIs()
    {
        var (realm, bob, _) = await BobInRoomAsync(2);
        using var _r = realm;

        var away = await realm.Hub(bob).Heal();
        Assert.False(away.Success);
        Assert.Equal("no_healer_here", away.Reason);

        Assert.True(await realm.Hub(bob).JoinRoom(1));
        Assert.True((await realm.Hub(bob).Heal()).Success);
    }

    // ── F6 saving on character switch ────────────────────────────────────

    [Fact]
    public async Task SwitchingCharacters_SavesTheOneBeingLeft()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");
        // A second character for the same account
        using (var db = realm.NewDb())
        {
            await new Myria.Server.Realm.Repositories.SqlCharacterRepository(db).SaveAsync("bob",
                new Myria.Lib.Core.Entities.Characters.Character("Bobalt", new Myria.Lib.Core.Entities.Stats()));
        }
        realm.Session.Get(bob.ConnectionId)!.Money.TryAdd(4321);

        Assert.True(await realm.Hub(bob).LoadCharacter("Bobalt"));

        using var check = realm.NewDb();
        Assert.Equal(4321, check.Characters.Single(c => c.Name == "Bobchar").MoneyBronze);
    }

    // ── Player shop: the normal purchase still works (pins the A06/A10 rewrite) ──

    [Fact]
    public async Task ShopPurchase_MovesItemsAndGold_AndSavesBothSides()
    {
        using var realm = await HubHarness.CreateAsync();
        var alice = realm.Connect("alice");
        var bob = realm.Connect("bob");
        await realm.Hub(alice).LoadCharacter("Alicechar");
        await realm.Hub(bob).LoadCharacter("Bobchar");
        var seller = realm.Session.Get(alice.ConnectionId)!;
        var buyer = realm.Session.Get(bob.ConnectionId)!;

        // Any city room the seller may enter
        int cityRoom = -1;
        foreach (var room in Myria.Lib.Core.Services.RoomService.AllRooms
                     .Where(r => Myria.Lib.Core.Services.Regestries.CityRegistry.GetCityByRoom(r) is not null))
            if (await realm.Hub(alice).JoinRoom(room.Id)) { cityRoom = room.Id; break; }
        Assert.NotEqual(-1, cityRoom);

        Assert.True(ItemFactory.TryCreateItem("iron_ore", out var ore) && ore is not null);
        ore!.StackSize = 10;
        Assert.True(seller.Inventory.AddItem(ore, seller));
        await realm.Hub(alice).OpenShop();
        Assert.True(await realm.Hub(alice).DepositShopItem("iron_ore", 5));
        await realm.Hub(alice).SetShopItemPrice("iron_ore", 7);

        buyer.Money.TryAdd(1000);
        long sellerBefore = seller.Money.Balance.BronzeTotal;
        await realm.Hub(bob).BuyFromShop("Alicechar", "iron_ore", 3);

        Assert.Equal(3, buyer.Inventory.Items.Where(i => i.Id == "iron_ore").Sum(i => i.StackSize));
        Assert.Equal(1000 - 21, buyer.Money.Balance.BronzeTotal);
        Assert.Equal(sellerBefore + 21, seller.Money.Balance.BronzeTotal);   // owner present: no remote fee
        using var check = realm.NewDb();
        Assert.Equal(1000 - 21, check.Characters.Single(c => c.Name == "Bobchar").MoneyBronze);
        Assert.Equal(2, check.PlayerShopItems.Single(i => i.ItemId == "iron_ore").Quantity);
    }

    // ── F7 + F6 + F10 trade ──────────────────────────────────────────────

    [Fact]
    public async Task Trade_GoldArrivesOneToOne_EvenWithFame_AndBothSidesAreSaved_Once()
    {
        using var realm = await HubHarness.CreateAsync();
        var alice = realm.Connect("alice");
        var bob = realm.Connect("bob");
        await realm.Hub(alice).LoadCharacter("Alicechar");
        await realm.Hub(bob).LoadCharacter("Bobchar");
        var a = realm.Session.Get(alice.ConnectionId)!;
        var b = realm.Session.Get(bob.ConnectionId)!;
        a.Money.TryAdd(1000);
        b.ActiveJobId = "woodcutter";
        JobManager.GetOrAdd(b, "woodcutter").FameXp = 50_000_000;   // top fame multiplier

        var (tradeId, _) = realm.Trade.Propose(alice.ConnectionId, "Alicechar", "Bobchar");
        Assert.True(realm.Trade.Accept(bob.ConnectionId, "Bobchar", tradeId).Ok);
        await realm.Hub(alice).SetTradeGold(300);
        await realm.Hub(alice).ConfirmTrade();
        await realm.Hub(bob).ConfirmTrade();
        await realm.Hub(bob).ConfirmTrade();   // a repeat must not execute the trade again

        Assert.Equal(700, a.Money.Balance.BronzeTotal);
        Assert.Equal(300, b.Money.Balance.BronzeTotal);
        using var check = realm.NewDb();
        Assert.Equal(700, check.Characters.Single(c => c.Name == "Alicechar").MoneyBronze);
        Assert.Equal(300, check.Characters.Single(c => c.Name == "Bobchar").MoneyBronze);
    }
}

/// <summary>Unit-level pieces of A06 F9/F10.</summary>
public class SessionAndLockTests
{
    [Fact]
    public void GatherLimit_SurvivesSessionRemovalAndANewSession()
    {
        var session = new CharacterSessionService();
        var c = new Myria.Lib.Core.Entities.Characters.Character("Gatherer", new Myria.Lib.Core.Entities.Stats());

        session.TryAdd("conn-1", c);
        Assert.True(session.TryConsumeGather("Gatherer", 5, dailyLimit: 1, out _));
        session.Remove("conn-1");
        session.TryAdd("conn-2", c);   // reconnect

        Assert.False(session.TryConsumeGather("Gatherer", 5, dailyLimit: 1, out _));
    }

    [Fact]
    public void TryClaimCompleted_OnlyOnce_AndNotAfterAnOfferChange()
    {
        var trade = new TradeService();
        var (id, _) = trade.Propose("c1", "A", "B");
        trade.Accept("c2", "B", id);
        trade.SetGold("c1", 10);
        trade.Confirm("c1");
        trade.Confirm("c2");
        trade.SetGold("c1", 0);   // changed after both confirmed -> flags reset

        Assert.False(trade.TryClaimCompleted("c1", out _, out _));

        trade.Confirm("c1");
        trade.Confirm("c2");
        Assert.True(trade.TryClaimCompleted("c2", out var snap, out var partner));
        Assert.Equal("c1", partner);
        Assert.Equal(0, snap!.TheirGold);
        Assert.False(trade.TryClaimCompleted("c1", out _, out _));   // already executed
    }

    [Theory]
    [InlineData(4_611_686_018_427_387_905L, false)]   // 2^62+1: the overflow price from the report
    [InlineData(PlayerShopService.MaxUnitPrice + 1, false)]
    [InlineData(PlayerShopService.MaxUnitPrice, true)]
    [InlineData(0L, false)]
    public async Task ShopPrice_IsCapped(long price, bool accepted)
    {
        using var host = new RealmDbHost();
        var owner = new Myria.Server.Realm.Models.Character { UserId = "alice", Name = "Alicia" };
        host.Db.Characters.Add(owner);
        await host.Db.SaveChangesAsync();
        host.Db.PlayerShops.Add(new Myria.Server.Realm.Models.PlayerShop
        {
            OwnerCharacterId = owner.Id, RoomId = 1,
            Items = { new Myria.Server.Realm.Models.PlayerShopItem { ItemId = "iron_ore", Quantity = 2 } }
        });
        await host.Db.SaveChangesAsync();

        var (ok, error, _) = await new PlayerShopService(host.NewContext(), new CharacterPresenceService())
            .SetPriceAsync("Alicia", "iron_ore", price);

        Assert.Equal(accepted, ok);
        if (!accepted) Assert.Equal("invalid_price", error);
    }

    [Fact]
    public async Task CharacterLocks_SerialiseTheSameCharacter_AndAllowOthers()
    {
        var locks = new CharacterLocks();
        var first = await locks.AcquireAsync("Bob");

        var sameName = locks.AcquireAsync("bob", "Alice");   // waits for Bob
        var other = locks.AcquireAsync("Carol");
        Assert.True(other.IsCompletedSuccessfully);
        Assert.False(sameName.IsCompleted);

        first.Dispose();
        (await sameName).Dispose();
        (await other).Dispose();
    }
}
