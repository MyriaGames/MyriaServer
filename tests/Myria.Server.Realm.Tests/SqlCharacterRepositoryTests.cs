using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Builder;
using Myria.Server.Realm.Repositories;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// SqlCharacterRepository is the only thing standing between a player's progress and a normalised
/// SQL schema with sixteen child tables, and it hand-maps every field in both directions. A field
/// dropped from one direction and not the other is invisible until a player logs back in and finds
/// their gold, gear or quest progress gone — the most damaging class of bug this project can ship,
/// and the one least likely to be caught by playing the game for five minutes.
///
/// So these tests all do the same thing: build a character, save it, load it back through a fresh
/// DbContext, and assert the value survived. Reading through a second context matters — the same
/// context could answer from its change tracker and hide a mapping that never reached SQL.
/// </summary>
[Collection("RealmDb")]
public class SqlCharacterRepositoryTests
{
    private const string User = "alice";

    public SqlCharacterRepositoryTests(RealmDbFixture _) { }

    /// <summary>Saves the character, then loads it back through a context that has never seen it.</summary>
    private static async Task<Character?> RoundTripAsync(RealmDbHost host, Character character)
    {
        await new SqlCharacterRepository(host.Db).SaveAsync(User, character);

        using var readContext = host.NewContext();
        return await new SqlCharacterRepository(readContext).LoadAsync(User, character.Name);
    }

    private static Character NewCharacter(string name = "Testchar") =>
        new(name, new Stats());

    // ---- Identity and progression ------------------------------------------------------------

    [Fact]
    public async Task SaveThenLoad_RestoresLevelAndExperience()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.Level = 27;
        character.Experience = 123_456;
        character.ExpForNextLvl = 200_000;

        var loaded = await RoundTripAsync(host, character);

        Assert.NotNull(loaded);
        Assert.Equal(27, loaded!.Level);
        Assert.Equal(123_456, loaded.Experience);
        Assert.Equal(200_000, loaded.ExpForNextLvl);
    }

    /// <summary>
    /// Every stat is mapped by hand, base and bonus separately. Distinct values per field catch a
    /// copy-paste transposition (spirit written into intelligence, say) that equal values would hide.
    /// </summary>
    [Fact]
    public async Task SaveThenLoad_RestoresEveryStatWithoutTransposingThem()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.Stats.Strength = 11;
        character.Stats.Dexterity = 22;
        character.Stats.Endurance = 33;
        character.Stats.Intelligence = 44;
        character.Stats.Spirit = 55;
        character.Stats.StrengthBonus = 1;
        character.Stats.DexterityBonus = 2;
        character.Stats.EnduranceBonus = 3;
        character.Stats.IntelligenceBonus = 4;
        character.Stats.SpiritBonus = 5;
        character.Stats.UnusedPoints = 7;
        character.Stats.BaseHealth = 250;
        character.Stats.BaseMana = 120;

        var loaded = await RoundTripAsync(host, character);

        Assert.NotNull(loaded);
        Assert.Equal(11, loaded!.Stats.Strength);
        Assert.Equal(22, loaded.Stats.Dexterity);
        Assert.Equal(33, loaded.Stats.Endurance);
        Assert.Equal(44, loaded.Stats.Intelligence);
        Assert.Equal(55, loaded.Stats.Spirit);
        Assert.Equal(1, loaded.Stats.StrengthBonus);
        Assert.Equal(2, loaded.Stats.DexterityBonus);
        Assert.Equal(3, loaded.Stats.EnduranceBonus);
        Assert.Equal(4, loaded.Stats.IntelligenceBonus);
        Assert.Equal(5, loaded.Stats.SpiritBonus);
        Assert.Equal(250, loaded.Stats.BaseHealth);
        Assert.Equal(120, loaded.Stats.BaseMana);
    }

    /// <summary>
    /// Current HP/MP are what a player is mid-fight or mid-dungeon with; silently restoring them
    /// to full (or to zero) on relog is a visible correctness bug either way.
    /// </summary>
    [Fact]
    public async Task SaveThenLoad_RestoresCurrentHealthAndMana()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.Stats.BaseHealth = 300;
        character.Stats.BaseMana = 150;
        character.CurrentHealth = 42;
        character.CurrentMana = 17;

        var loaded = await RoundTripAsync(host, character);

        Assert.Equal(42, loaded!.CurrentHealth);
        Assert.Equal(17, loaded.CurrentMana);
    }

    [Fact]
    public async Task SaveThenLoad_RestoresTheCurrentRoom()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.CurrentRoomId = 1;
        character.LastHealerRoomId = 1;

        var loaded = await RoundTripAsync(host, character);

        Assert.Equal(1, loaded!.CurrentRoomId);
        Assert.Equal(1, loaded.LastHealerRoomId);
    }

    // ---- Money ------------------------------------------------------------------------------

    /// <summary>
    /// Money is stored as a single bronze total and rebuilt into a MoneyBag on load. Any rounding
    /// or unit slip here is a direct economy bug, so the exact total has to survive.
    /// </summary>
    [Fact]
    public async Task SaveThenLoad_RestoresTheExactGoldBalance()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.Money.Balance = new Money(1_234_567);

        var loaded = await RoundTripAsync(host, character);

        Assert.Equal(1_234_567, loaded!.Money.Balance.BronzeTotal);
    }

    [Fact]
    public async Task SaveThenLoad_RestoresAZeroBalance()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        character.Money.Balance = new Money(0);

        var loaded = await RoundTripAsync(host, character);

        Assert.Equal(0, loaded!.Money.Balance.BronzeTotal);
    }

    // ---- Inventory --------------------------------------------------------------------------

    /// <summary>
    /// Items are persisted as id + stack size and rebuilt through ItemFactory. Stack sizes are the
    /// easy thing to lose (every item comes back as a single) — that silently deletes materials.
    /// </summary>
    [Fact]
    public async Task SaveThenLoad_RestoresInventoryItemsWithTheirStackSizes()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();

        var potion = ItemFactory.CreateItem("t1_healing_potion", 7);
        Assert.NotNull(potion);
        character.Inventory.Items.Add(potion!);

        var loaded = await RoundTripAsync(host, character);

        var restored = Assert.Single(loaded!.Inventory.Items);
        Assert.Equal("t1_healing_potion", restored.Id);
        Assert.Equal(7, restored.StackSize);
    }

    /// <summary>
    /// Slot order is persisted explicitly and reapplied with OrderBy(SlotIndex). If it were ever
    /// left to the database's natural ordering, a player's carefully arranged bags would reshuffle
    /// on every relog.
    /// </summary>
    [Fact]
    public async Task SaveThenLoad_PreservesInventorySlotOrder()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();

        var first = ItemFactory.CreateItem("t1_healing_potion", 1);
        var second = ItemFactory.CreateItem("t1_mana_potion", 1);
        Assert.NotNull(first);
        Assert.NotNull(second);

        character.Inventory.Items.Add(second!);
        character.Inventory.Items.Add(first!);

        var loaded = await RoundTripAsync(host, character);

        Assert.Equal(
            ["t1_mana_potion", "t1_healing_potion"],
            loaded!.Inventory.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task SaveThenLoad_RestoresAnEmptyInventoryAsEmpty()
    {
        using var host = new RealmDbHost();

        var loaded = await RoundTripAsync(host, NewCharacter());

        Assert.Empty(loaded!.Inventory.Items);
    }

    // ---- Overwriting an existing save ---------------------------------------------------------

    /// <summary>
    /// Saving twice must replace the child rows, not append to them. Without the Clear() before
    /// each rebuild, a player's inventory would double on every single save — the kind of bug that
    /// looks like item duplication and corrupts an economy fast.
    /// </summary>
    [Fact]
    public async Task SavingTwice_ReplacesInventoryRatherThanAppendingToIt()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        var character = NewCharacter();

        var potion = ItemFactory.CreateItem("t1_healing_potion", 1);
        Assert.NotNull(potion);
        character.Inventory.Items.Add(potion!);

        await repository.SaveAsync(User, character);
        await repository.SaveAsync(User, character);

        using var readContext = host.NewContext();
        var loaded = await new SqlCharacterRepository(readContext).LoadAsync(User, character.Name);

        Assert.Single(loaded!.Inventory.Items);
    }

    /// <summary>A second save must update the existing row, never insert a duplicate character.</summary>
    [Fact]
    public async Task SavingTwice_UpdatesTheSameRowInsteadOfInsertingASecond()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        var character = NewCharacter();

        await repository.SaveAsync(User, character);
        character.Level = 40;
        await repository.SaveAsync(User, character);

        using var readContext = host.NewContext();
        Assert.Single(readContext.Characters.Where(c => c.Name == character.Name));

        var loaded = await new SqlCharacterRepository(readContext).LoadAsync(User, character.Name);
        Assert.Equal(40, loaded!.Level);
    }

    // ---- Ownership --------------------------------------------------------------------------

    /// <summary>
    /// Ownership is keyed on the username from the caller's JWT. Loading must be scoped to it, or
    /// knowing a character's name would be enough to load somebody else's character.
    /// </summary>
    [Fact]
    public async Task Load_RefusesACharacterOwnedByAnotherAccount()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        await new SqlCharacterRepository(host.Db).SaveAsync(User, character);

        using var readContext = host.NewContext();
        var loaded = await new SqlCharacterRepository(readContext).LoadAsync("mallory", character.Name);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_ReturnsNullForAnUnknownCharacterName()
    {
        using var host = new RealmDbHost();

        var loaded = await new SqlCharacterRepository(host.Db).LoadAsync(User, "NoSuchHero");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task GetNames_ListsOnlyTheCallersOwnCharacters()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        await repository.SaveAsync(User, NewCharacter("Alpha"));
        await repository.SaveAsync(User, NewCharacter("Beta"));
        await repository.SaveAsync("mallory", NewCharacter("Gamma"));

        var names = await repository.GetNamesAsync(User);

        Assert.Equal(2, names.Count);
        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
        Assert.DoesNotContain("Gamma", names);
    }

    // ---- Deletion ---------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheCharacter()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        var character = NewCharacter();
        await repository.SaveAsync(User, character);

        await repository.DeleteAsync(User, character.Name);

        Assert.Null(await repository.LoadAsync(User, character.Name));
    }

    /// <summary>
    /// Deletion is ownership-scoped too — otherwise any account could delete any character just by
    /// naming it.
    /// </summary>
    [Fact]
    public async Task Delete_RefusesToRemoveAnotherAccountsCharacter()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        var character = NewCharacter();
        await repository.SaveAsync(User, character);

        await repository.DeleteAsync("mallory", character.Name);

        Assert.NotNull(await repository.LoadAsync(User, character.Name));
    }

    /// <summary>
    /// Character children cascade-delete. If that ever regressed, deleting a character would leave
    /// orphaned inventory rows behind and slowly bloat the database.
    /// </summary>
    [Fact]
    public async Task Delete_AlsoRemovesTheCharactersInventoryRows()
    {
        using var host = new RealmDbHost();
        var repository = new SqlCharacterRepository(host.Db);
        var character = NewCharacter();

        var potion = ItemFactory.CreateItem("t1_healing_potion", 3);
        Assert.NotNull(potion);
        character.Inventory.Items.Add(potion!);
        await repository.SaveAsync(User, character);

        await repository.DeleteAsync(User, character.Name);

        using var readContext = host.NewContext();
        Assert.Empty(readContext.CharacterInventoryItems);
    }

    [Fact]
    public async Task Delete_IsANoOpForAnUnknownCharacter()
    {
        using var host = new RealmDbHost();

        // Must not throw.
        await new SqlCharacterRepository(host.Db).DeleteAsync(User, "NoSuchHero");
    }
    // ---- Equipment upgrade state (wasn't stored at all: every upgrade was lost on load) --------

    private static Myria.Lib.Core.Entities.Items.EquipmentItem Upgraded(string id, int level, float quality)
    {
        var item = (Myria.Lib.Core.Entities.Items.EquipmentItem)ItemFactory.CreateItem(id)!;
        item.CraftQuality = quality;
        for (int i = 0; i < level; i++) item.TryUpgrade_Internal();
        return item;
    }

    [Fact]
    public async Task SaveThenLoad_KeepsUpgradeLevelAndCraftQuality_InTheBagAndEquipped()
    {
        using var host = new RealmDbHost();
        var character = NewCharacter();
        var bagSword = Upgraded("iron_sword", 3, 1.2f);
        character.Inventory.Items.Add(bagSword);
        character.Inventory.Items.Add(Upgraded("iron_sword", 0, 1f));
        character.WeaponSlot = Upgraded("steel_sword", 2, 1.1f);
        character.ArmorSlot = Upgraded("chain_armor", 1, 1f);

        var loaded = await RoundTripAsync(host, character);

        var swords = loaded!.Inventory.Items.OfType<Myria.Lib.Core.Entities.Items.EquipmentItem>().Where(i => i.Id == "iron_sword").ToList();
        Assert.Equal(new[] { 0, 3 }, swords.Select(s => s.UpgradeLevel).OrderBy(l => l));
        var plus3 = swords.Single(s => s.UpgradeLevel == 3);
        Assert.Equal(1.2f, plus3.CraftQuality, 3);
        Assert.Equal(bagSword.BonusATK, plus3.BonusATK);
        Assert.Equal(2, loaded.WeaponSlot!.UpgradeLevel);
        Assert.Equal(1.1f, loaded.WeaponSlot.CraftQuality, 3);
        Assert.Equal(character.WeaponSlot.BonusATK, loaded.WeaponSlot.BonusATK);
        Assert.Equal(1, loaded.ArmorSlot!.UpgradeLevel);
    }
}
