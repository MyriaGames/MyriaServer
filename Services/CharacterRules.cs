using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Server.Realm.Services
{
    // What a client may decide about a character through the REST save (security review 2026-11-26,
    // A06 F1): the save used to write the whole character from the request - level, stats, items,
    // gold, quests - so one request made any character as strong and rich as wanted. The client only
    // ever needs three things, all of them reproduced here with the same Myria.Lib calls the WPF
    // client uses, so installed clients keep working:
    //   1. creating a character from name + race + class (ViewModel_CharacterCreationPage.Create),
    //   2. the one-time race choice for a character created before races existed (RaceSelected false),
    //   3. the forced class change away from the removed RunicMage class.
    // Everything else a character gains comes from server-side game actions (GameHub).
    public static class CharacterRules
    {
        public static bool IsSelectableRace(string? race) =>
            !string.IsNullOrWhiteSpace(race) && RaceProfile.All.ContainsKey(race);

        public static bool IsSelectableClass(string? race, string? cls) =>
            !string.IsNullOrWhiteSpace(cls) &&
            ClassProfile.All.ContainsKey(cls) &&
            !cls.Equals(CharacterClass.RunicMage, StringComparison.OrdinalIgnoreCase) &&
            (race is null || ClassManager.IsClassAllowed(race, cls));

        /// <summary>A brand-new character, exactly as the client's creation page builds it.</summary>
        public static Character CreateNew(string name, string race, string cls)
        {
            var raceProfile = RaceProfile.All[race];
            var stats = new Stats
            {
                Strength     = 10 + raceProfile.BaseStatBonus["STR"],
                Dexterity    = 10 + raceProfile.BaseStatBonus["DEX"],
                Endurance    = 10 + raceProfile.BaseStatBonus["END"],
                Intelligence = 10 + raceProfile.BaseStatBonus["INT"],
                Spirit       = 10 + raceProfile.BaseStatBonus["SPR"],
                BaseHealth   = 30 + raceProfile.BaseHpBonus,
                BaseMana     = 30 + raceProfile.BaseManaBonus,
            };

            var character = new Character(name, stats) { Race = race, RaceSelected = true, Class = cls };
            StartingEquipmentService.GrantStartingEquipment(character);
            SkillFactory.UpdateSkills(character);
            SkillSlotService.MigrateIfEmpty(character);
            character.CurrentHealth = character.MaxHealth;
            character.CurrentMana   = character.MaxMana;
            character.CurrentRoom   = RoomService.GetRoomById(1);
            character.CurrentRoomId = character.CurrentRoom.Id;
            return character;
        }

        /// <summary>The one-time race choice: moves the race bonuses from the old race to the new one
        /// (same as the client's ViewModel_RaceSelectionPage.ApplyRaceMigration). Only while RaceSelected
        /// is false; returns whether anything changed.</summary>
        public static bool TryChooseRace(Character character, string newRace)
        {
            if (character.RaceSelected || !IsSelectableRace(newRace)) return false;

            var from = character.Race;
            if (!from.Equals(newRace, StringComparison.OrdinalIgnoreCase) &&
                RaceProfile.All.TryGetValue(from, out var oldProfile) &&
                RaceProfile.All.TryGetValue(newRace, out var newProfile))
            {
                character.Stats.Strength     += newProfile.BaseStatBonus["STR"] - oldProfile.BaseStatBonus["STR"];
                character.Stats.Dexterity    += newProfile.BaseStatBonus["DEX"] - oldProfile.BaseStatBonus["DEX"];
                character.Stats.Endurance    += newProfile.BaseStatBonus["END"] - oldProfile.BaseStatBonus["END"];
                character.Stats.Intelligence += newProfile.BaseStatBonus["INT"] - oldProfile.BaseStatBonus["INT"];
                character.Stats.Spirit       += newProfile.BaseStatBonus["SPR"] - oldProfile.BaseStatBonus["SPR"];
                character.Stats.BaseHealth   += newProfile.BaseHpBonus   - oldProfile.BaseHpBonus;
                character.Stats.BaseMana     += newProfile.BaseManaBonus - oldProfile.BaseManaBonus;
            }
            character.Race = newRace;
            character.RaceSelected = true;
            return true;
        }

        /// <summary>The forced class change away from RunicMage (same as ViewModel_ClassReselectionPage:
        /// the normal class-change cooldown is skipped). Only while the class is RunicMage.</summary>
        public static bool TryLeaveRunicMage(Character character, string newClass)
        {
            if (!character.Class.Equals(CharacterClass.RunicMage, StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsSelectableClass(character.Race, newClass)) return false;
            character.LastClassChanged = DateTime.MinValue;
            return ClassManager.SetClass(character, newClass);
        }
    }
}
