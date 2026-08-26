namespace Myria.Server.Realm.Models.Dto.Public
{
    /// <summary>Row shown in the public character registry list.</summary>
    public class PublicCharacterListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public string Class { get; set; } = "";
        public string Race { get; set; } = "";
        public DateTime LastSaved { get; set; }
    }

    /// <summary>Full public profile for one character, including equipped gear.</summary>
    public class PublicCharacterDetail
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public string Class { get; set; } = "";
        public string Race { get; set; } = "";
        public DateTime LastSaved { get; set; }

        public int StatBaseHealth { get; set; }
        public int StatBaseMana { get; set; }

        public int StatStrength { get; set; }
        public int StatDexterity { get; set; }
        public int StatEndurance { get; set; }
        public int StatIntelligence { get; set; }
        public int StatSpirit { get; set; }

        public int StatStrengthBonus { get; set; }
        public int StatDexterityBonus { get; set; }
        public int StatEnduranceBonus { get; set; }
        public int StatIntelligenceBonus { get; set; }
        public int StatSpiritBonus { get; set; }

        public PublicItemDto? Weapon { get; set; }
        public PublicItemDto? Armor { get; set; }
        public PublicItemDto? Accessory { get; set; }
    }

    /// <summary>Public view of an equipped item's data.</summary>
    public class PublicItemDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Rarity { get; set; } = "Common";

        public int? BaseBonusATK { get; set; }
        public int? BaseBonusDEF { get; set; }
        public int? BaseBonusMATK { get; set; }
        public int? BaseBonusMDEF { get; set; }
        public int? BaseBonusHP { get; set; }
        public int? BaseBonusMP { get; set; }
        public int? BaseBonusSTR { get; set; }
        public int? BaseBonusDEX { get; set; }
        public int? BaseBonusEND { get; set; }
        public int? BaseBonusINT { get; set; }
        public int? BaseBonusSPR { get; set; }
        public int? BaseBonusEvasion { get; set; }
    }
}
