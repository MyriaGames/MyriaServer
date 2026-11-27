using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Same-id equipment at different upgrade levels: equipping the +6 blade must equip the +6 one,
/// not whichever blade comes first in the bag (EquipItem only knows the item id).
/// </summary>
[Collection("RealmDb")]
public class EquipInstanceTests
{
    private static EquipmentItem Blade(int level)
    {
        var e = (EquipmentItem)ItemFactory.CreateItem("iron_sword", 1, level, 1f)!;
        e.AllowedClasses.Clear();   // usable by the test character's class
        return e;
    }

    [Fact]
    public async Task EquipItemInstance_EquipsTheChosenUpgradeLevel_AndLeavesTheOthers()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var player = realm.Session.Get(bob.ConnectionId)!;
        player.Inventory.Items.RemoveAll(i => i.Id == "iron_sword");
        player.Inventory.Items.AddRange(new[] { Blade(4), Blade(6), Blade(10) });

        var result = await realm.Hub(bob).EquipItemInstance("iron_sword", 6, 1f);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(6, player.WeaponSlot!.UpgradeLevel);
        var left = player.Inventory.Items.OfType<EquipmentItem>().Where(i => i.Id == "iron_sword")
            .Select(i => i.UpgradeLevel).OrderBy(l => l).ToArray();
        Assert.Equal(new[] { 4, 10 }, left);

        // A second blade swap keeps the bag consistent: +10 in, +6 back out.
        Assert.True((await realm.Hub(bob).EquipItemInstance("iron_sword", 10, 1f)).Success);
        Assert.Equal(10, player.WeaponSlot!.UpgradeLevel);
        Assert.Contains(player.Inventory.Items.OfType<EquipmentItem>(), i => i.Id == "iron_sword" && i.UpgradeLevel == 6);
    }

    [Fact]
    public async Task EquipItemInstance_UnknownUpgradeLevel_Fails()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        var player = realm.Session.Get(bob.ConnectionId)!;
        player.Inventory.Items.Add(Blade(4));

        Assert.False((await realm.Hub(bob).EquipItemInstance("iron_sword", 7, 1f)).Success);
    }
}
