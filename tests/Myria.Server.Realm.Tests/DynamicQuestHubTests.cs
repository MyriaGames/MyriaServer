using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.World;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// v0.3 quest rework, part 2, through the real hub and game data: the Adventurers' Guild, bounties
/// for overrun rooms near a guild house, supply requests from NPCs low on stock, how a gap is shared
/// between players, hand-ins, staleness and persistence. Room 1 (Royas) has the guild house and
/// smith_default; room 2, one exit north, is an overworld room with monsters 33-36.
/// </summary>
[Collection("RealmDb")]
public class DynamicQuestHubTests
{
    private sealed record Realm2(HubHarness Realm, FakeCallerContext Alice, FakeCallerContext Bob, Character A, Character B) : IDisposable
    {
        public void Dispose() => Realm.Dispose();
    }

    private static async Task<Realm2> BothInRoomAsync(int roomId)
    {
        var realm = await HubHarness.CreateAsync();
        var alice = realm.Connect("alice");
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(alice).LoadCharacter("Alicechar"));
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.True(await realm.PlaceAsync(alice, roomId));
        Assert.True(await realm.PlaceAsync(bob, roomId));
        return new Realm2(realm, alice, bob, realm.Session.Get(alice.ConnectionId)!, realm.Session.Get(bob.ConnectionId)!);
    }

    /// <summary>Ticks until room 2's monster 33 is overrun.</summary>
    private static void Overrun(WorldState world)
    {
        while (world.PopulationGap(new PopulationKey(2, 33)) == 0) world.Tick();
    }

    /// <summary>Ticks until the Royas smith is short on iron ore (it drains passively).</summary>
    private static void SmithRunsLow(WorldState world)
    {
        while (world.StockGap(new StockKey(1, "smith_default", "iron_ore")) == 0) world.Tick();
    }

    private static void Give(Character c, string itemId, int amount)
    {
        var item = ItemFactory.CreateItem(itemId)!;
        item.StackSize = amount;
        c.Inventory.AddItem(item, c);
    }

    // ── Adventurers' Guild ────────────────────────────────────────────────

    [Fact]
    public async Task JoiningTheGuild_NeedsAGuildHouse_AndIsSaved()
    {
        using var r = await BothInRoomAsync(2);
        Assert.False(await r.Realm.Hub(r.Alice).JoinAdventurersGuild()); // no guild house in room 2

        Assert.True(await r.Realm.Hub(r.Alice).JoinRoom(1));
        Assert.True(await r.Realm.Hub(r.Alice).JoinAdventurersGuild());

        Assert.True(r.A.AdventurersGuildMember);
        await using var db = r.Realm.NewDb();
        Assert.True((await db.Characters.SingleAsync(c => c.Name == "Alicechar")).AdventurersGuildMember);
    }

    [Fact]
    public async Task Bounties_AreOnlyForMembers_AndOnlyForOverrunRoomsNearby()
    {
        using var r = await BothInRoomAsync(1);
        Overrun(r.Realm.World.State!);

        Assert.Empty(await r.Realm.Hub(r.Alice).GetDynamicQuestOffers(DynamicQuestRules.GuildNpcId));

        await r.Realm.Hub(r.Alice).JoinAdventurersGuild();
        var offers = await r.Realm.Hub(r.Alice).GetDynamicQuestOffers(DynamicQuestRules.GuildNpcId);

        Assert.Contains(offers, o => o.Preview.RequiredRoomId == 2 && o.Preview.RequiredKills.ContainsKey(33));
        Assert.All(offers, o => Assert.Contains(o.Preview.RequiredRoomId!.Value, DynamicQuests.RoomsWithin(1, DynamicQuestRules.GuildRadius)));
    }

    [Fact]
    public async Task ABounty_IsSharedBetweenPlayers_OneGrantEach_AndStopsOnceFullyPromised()
    {
        using var r = await BothInRoomAsync(1);
        var world = r.Realm.World.State!;
        Overrun(world);
        var key = new PopulationKey(2, 33);
        int gap = world.PopulationGap(key);
        await r.Realm.Hub(r.Alice).JoinAdventurersGuild();
        await r.Realm.Hub(r.Bob).JoinAdventurersGuild();
        var token = DynamicQuests.Token(key);

        var first = await r.Realm.Hub(r.Alice).AcceptDynamicQuest(token);
        Assert.NotNull(first);
        Assert.Null(await r.Realm.Hub(r.Alice).AcceptDynamicQuest(token));   // one per player per gap

        var second = await r.Realm.Hub(r.Bob).AcceptDynamicQuest(token);
        Assert.NotNull(second);
        Assert.True(first!.RequiredKills[33] + second!.RequiredKills[33] <= gap);
        Assert.Contains(r.A.ActiveQuests, q => q.Id == first.Id);
    }

    [Fact]
    public async Task BountyKills_CountOnlyInTheirRoom_AndAreHandedInAtAGuildHouse()
    {
        using var r = await BothInRoomAsync(1);
        Overrun(r.Realm.World.State!);
        await r.Realm.Hub(r.Alice).JoinAdventurersGuild();
        var dto = (await r.Realm.Hub(r.Alice).AcceptDynamicQuest(DynamicQuests.Token(new PopulationKey(2, 33))))!;
        int target = dto.RequiredKills[33];
        long goldBefore = r.A.Money.Balance.BronzeTotal;

        r.A.CurrentRoomId = 1;                                      // a kill elsewhere doesn't count
        QuestManager.UpdateKillProgress(r.A, 33);
        r.A.CurrentRoomId = 2;
        for (int i = 0; i < target; i++) QuestManager.UpdateKillProgress(r.A, 33);

        Assert.True(await r.Realm.Hub(r.Alice).QuestAction(dto.Id, isReturn: true)); // Alice stands in room 1 (guild house)

        Assert.DoesNotContain(r.A.ActiveQuests, q => q.Id == dto.Id);
        Assert.Equal(goldBefore + dto.RewardGold, r.A.Money.Balance.BronzeTotal);
        Assert.Null(r.Realm.Quests.Get(DynamicQuests.GrantIdOf(dto.Id)));
    }

    // ── Supply requests ───────────────────────────────────────────────────

    [Fact]
    public async Task ASupplyRequest_TakesTheItems_AndRestocksThatNpc()
    {
        using var r = await BothInRoomAsync(1);
        var world = r.Realm.World.State!;
        SmithRunsLow(world);
        var stockKey = new StockKey(1, "smith_default", "iron_ore");

        var offer = Assert.Single(await r.Realm.Hub(r.Alice).GetDynamicQuestOffers("smith_default"),
            o => o.Preview.RequiredItems.ContainsKey("iron_ore"));
        var dto = (await r.Realm.Hub(r.Alice).AcceptDynamicQuest(offer.Token))!;
        int need = dto.RequiredItems["iron_ore"];
        Give(r.A, "iron_ore", need + 2);
        int stockBefore = world.GetStock(stockKey);

        Assert.True(await r.Realm.Hub(r.Alice).QuestAction(dto.Id, isReturn: true));

        Assert.Equal(2, r.A.Inventory.Items.Where(i => i.Id == "iron_ore").Sum(i => i.StackSize));
        Assert.Equal(stockBefore + need, world.GetStock(stockKey));
    }

    [Fact]
    public async Task ASupplyRequest_CantBeHandedInToTheSameNpcTypeInAnotherCity()
    {
        using var r = await BothInRoomAsync(1);
        SmithRunsLow(r.Realm.World.State!);
        var offer = (await r.Realm.Hub(r.Alice).GetDynamicQuestOffers("smith_default")).First(o => o.Preview.RequiredItems.ContainsKey("iron_ore"));
        var dto = (await r.Realm.Hub(r.Alice).AcceptDynamicQuest(offer.Token))!;
        Give(r.A, "iron_ore", dto.RequiredItems["iron_ore"]);
        r.A.Level = 10;
        Assert.True(await r.Realm.PlaceAsync(r.Alice, 12)); // Hydea's northern gate also has a smith_default

        Assert.False(await r.Realm.Hub(r.Alice).QuestAction(dto.Id, isReturn: true));
    }

    [Fact]
    public async Task DynamicQuests_CantBeAcceptedByIdThroughTheNormalQuestFlow()
    {
        using var r = await BothInRoomAsync(1);
        SmithRunsLow(r.Realm.World.State!);
        var offer = (await r.Realm.Hub(r.Alice).GetDynamicQuestOffers("smith_default")).First();
        var dto = (await r.Realm.Hub(r.Alice).AcceptDynamicQuest(offer.Token))!;

        Assert.False(await r.Realm.Hub(r.Bob).QuestAction(dto.Id, isReturn: false));
    }

    // ── Staleness and persistence ─────────────────────────────────────────

    [Fact]
    public async Task AGrantUnfinishedForAWeek_IsOfferedAgain_WhileTheHolderKeepsTheirs()
    {
        using var r = await BothInRoomAsync(1);
        var world = r.Realm.World.State!;
        Overrun(world);
        var key = new PopulationKey(2, 33);
        var token = DynamicQuests.Token(key);
        await r.Realm.Hub(r.Alice).JoinAdventurersGuild();
        var now = DateTime.UtcNow;
        r.Realm.Quests.Clock = () => now;

        // Alice takes a share; other players take the rest, so a newcomer is offered nothing...
        var a = await r.Realm.Hub(r.Alice).AcceptDynamicQuest(token);
        Assert.NotNull(a);
        for (int i = 0; await r.Realm.Quests.AcceptAsync(world, token, $"Other{i}") is not null; i++) { }
        Assert.DoesNotContain(r.Realm.Quests.BountyOffers(world, 1, "Newcomer"), o => o.Token == token);

        // ...until a week passes: the unfinished shares are open again, and Alice's copy still stands.
        r.Realm.Quests.Clock = () => now + DynamicQuestRules.StaleAfter + TimeSpan.FromMinutes(1);
        Assert.Contains(r.Realm.Quests.BountyOffers(world, 1, "Newcomer"), o => o.Token == token);
        Assert.Contains(r.A.ActiveQuests, q => q.Id == a!.Id);
    }

    [Fact]
    public async Task OpenGrants_AndHeldQuests_SurviveARestart()
    {
        using var r = await BothInRoomAsync(1);
        Overrun(r.Realm.World.State!);
        await r.Realm.Hub(r.Alice).JoinAdventurersGuild();
        var dto = (await r.Realm.Hub(r.Alice).AcceptDynamicQuest(DynamicQuests.Token(new PopulationKey(2, 33))))!;
        await r.Realm.Hub(r.Alice).SaveSession();

        QuestManager.UnregisterDynamic(dto.Id); // as if the process restarted
        var reloaded = new DynamicQuestService(r.Realm.ScopeFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DynamicQuestService>.Instance);
        await reloaded.LoadAsync();

        Assert.NotNull(reloaded.Get(DynamicQuests.GrantIdOf(dto.Id)));
        await using var db = r.Realm.NewDb();
        var repo = new Myria.Server.Realm.Repositories.SqlCharacterRepository(db);
        var loaded = await repo.LoadAsync("alice", "Alicechar");
        Assert.Contains(loaded!.ActiveQuests, q => q.Id == dto.Id && q.RequiredRoomId == 2);
    }
}
