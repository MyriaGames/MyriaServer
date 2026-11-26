using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Reported 2026-11-26: quest counters were wrong and finished quests not returnable. On the server: items already
/// carried at accept time count, and the hand-in checks what the character carries now, not a counter that only
/// ever went up.
/// </summary>
[Collection("RealmDb")]
public class QuestProgressHubTests
{
    public QuestProgressHubTests(RealmDbFixture _) { }

    [Fact]
    public async Task CarriedItemsCount_AtAccept_AndTheHandInChecksTheBagNow()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var player = realm.Session.Get(bob.ConnectionId)!;
        player.CurrentRoom = Myria.Lib.Core.Services.RoomService.AllRooms.First(r => r.Id == 1);
        await realm.Hub(bob).JoinRoom(1);

        // co1_gather_ore (4 iron ore at the smith) needs co1_first_day done first.
        Assert.True(await realm.Hub(bob).QuestAction("co1_first_day", false));
        Assert.True(await realm.Hub(bob).QuestAction("co1_first_day", true));

        var ore = new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 4 };
        player.Inventory.Items.Add(ore);
        Assert.True(await realm.Hub(bob).QuestAction("co1_gather_ore", false));

        var quest = player.ActiveQuests.Single(q => q.Id == "co1_gather_ore");
        Assert.Equal(4, quest.ItemProgress["iron_ore"]);              // used to show 0/4
        Assert.Equal(QuestStatus.Completed, quest.Status);

        player.Inventory.Items.Remove(ore);                            // sold before handing in
        Assert.False(await realm.Hub(bob).QuestAction("co1_gather_ore", true));

        player.Inventory.Items.Add(new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 4 });
        Assert.True(await realm.Hub(bob).QuestAction("co1_gather_ore", true));
        Assert.Contains(player.CompletedQuests, q => q.Id == "co1_gather_ore");
    }
}
