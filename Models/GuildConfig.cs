namespace Myria.Server.Realm.Models
{
    public class GuildConfig
    {
        public int    HouseBaseGoldCost                  { get; set; } = 10;
        public double HouseCenterCostMultiplierPerLevel  { get; set; } = 0.10;
        public double RookieXpBoostPct                   { get; set; } = 0.07;
        public double RookieCommissionPct                { get; set; } = 0.03;

        /// <summary>Gold cost per additional room type in a guild house.</summary>
        public Dictionary<string, int> HouseRoomCosts { get; set; } = new();

        /// <summary>Material costs per room type when constructing a guild base room.</summary>
        public Dictionary<string, List<GuildRoomMaterialCost>> BaseRoomCosts { get; set; } = new();

        public long CalculateHousePrice(int prestigeLevel)
            => (long)(HouseBaseGoldCost * Math.Pow(1 + HouseCenterCostMultiplierPerLevel, prestigeLevel));
    }

    public class GuildRoomMaterialCost
    {
        public string ItemId { get; set; } = "";
        public int    Amount { get; set; }
    }
}
