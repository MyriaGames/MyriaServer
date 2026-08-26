using System.ComponentModel.DataAnnotations;

namespace Myria.Server.Realm.Models
{
    public enum GuildRank { Member = 1, Elite = 2, Ward = 3, Officer = 4, Leader = 5 }
    public enum GuildPropertyType { House, Base }
    public enum GuildRoomType { GatheringPlace, Deposit, Treasury, OfficersHall, LeaderOffice }
    public enum GuildApplicationMode { InviteOnly, OpenApplication, Both }

    public class Guild
    {
        public int Id { get; set; }
        [MaxLength(60)] public string Name { get; set; } = "";
        [MaxLength(5)]  public string Tag  { get; set; } = "";
        [MaxLength(300)] public string? Description { get; set; }
        public int LeaderCharacterId { get; set; }
        public Character LeaderCharacter { get; set; } = null!;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public GuildSettings  Settings  { get; set; } = null!;
        public GuildTreasury  Treasury  { get; set; } = null!;
        public GuildProperty? Property  { get; set; }

        public ICollection<GuildMember>      Members      { get; set; } = new List<GuildMember>();
        public ICollection<GuildRookie>      Rookies      { get; set; } = new List<GuildRookie>();
        public ICollection<GuildInvite>      Invites      { get; set; } = new List<GuildInvite>();
        public ICollection<GuildApplication> Applications { get; set; } = new List<GuildApplication>();
        public ICollection<GuildDeposit>     Deposit      { get; set; } = new List<GuildDeposit>();
    }

    public class GuildSettings
    {
        public int GuildId { get; set; }
        public Guild Guild { get; set; } = null!;
        public GuildApplicationMode ApplicationMode      { get; set; } = GuildApplicationMode.Both;
        public GuildRank            DepositWithdrawMinRank { get; set; } = GuildRank.Ward;
    }

    public class GuildTreasury
    {
        public int   GuildId { get; set; }
        public Guild Guild   { get; set; } = null!;
        public long  Balance { get; set; } = 0;
    }

    public class GuildDeposit
    {
        public int    Id       { get; set; }
        public int    GuildId  { get; set; }
        public Guild  Guild    { get; set; } = null!;
        [MaxLength(100)] public string ItemId { get; set; } = "";
        public int    Quantity { get; set; }
    }

    public class GuildMember
    {
        public int       GuildId     { get; set; }
        public Guild     Guild       { get; set; } = null!;
        public int       CharacterId { get; set; }
        public Character Character   { get; set; } = null!;
        public GuildRank Rank        { get; set; } = GuildRank.Member;
        public DateTime  JoinedAt    { get; set; } = DateTime.UtcNow;
    }

    public class GuildRookie
    {
        public int       Id                   { get; set; }
        public int       GuildId              { get; set; }
        public Guild     Guild                { get; set; } = null!;
        public int       CharacterId          { get; set; }
        public Character Character            { get; set; } = null!;
        public int       RecruiterCharacterId { get; set; }
        public Character RecruiterCharacter   { get; set; } = null!;
        public DateTime  HiredAt              { get; set; } = DateTime.UtcNow;
    }

    public class GuildInvite
    {
        public int       Id                  { get; set; }
        public int       GuildId             { get; set; }
        public Guild     Guild               { get; set; } = null!;
        public int       InviterCharacterId  { get; set; }
        public Character InviterCharacter    { get; set; } = null!;
        public int       InviteeCharacterId  { get; set; }
        public Character InviteeCharacter    { get; set; } = null!;
        public bool      IsRookieInvite      { get; set; } = false;
        public DateTime  CreatedAt           { get; set; } = DateTime.UtcNow;
        public DateTime  ExpiresAt           { get; set; } = DateTime.UtcNow.AddDays(7);
    }

    public class GuildApplication
    {
        public int       Id                   { get; set; }
        public int       GuildId              { get; set; }
        public Guild     Guild                { get; set; } = null!;
        public int       ApplicantCharacterId { get; set; }
        public Character ApplicantCharacter   { get; set; } = null!;
        [MaxLength(300)] public string? Message { get; set; }
        public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    }

    public class GuildProperty
    {
        public int             Id                { get; set; }
        public int             GuildId           { get; set; }
        public Guild           Guild             { get; set; } = null!;
        public GuildPropertyType Type            { get; set; }
        public int?            CityRoomId        { get; set; }
        public int?            BaseAnchorRoomId  { get; set; }
        public long?           PricePaid         { get; set; }
        public DateTime        AcquiredAt        { get; set; } = DateTime.UtcNow;

        public ICollection<GuildRoom> Rooms   { get; set; } = new List<GuildRoom>();
        public GuildHouseListing?     Listing { get; set; }
    }

    public class GuildRoom
    {
        public int           Id              { get; set; }
        public int           GuildPropertyId { get; set; }
        public GuildProperty GuildProperty   { get; set; } = null!;
        public int           RoomId          { get; set; }
        public string        Name            { get; set; } = "";
        public string        Description     { get; set; } = "";
        public GuildRoomType RoomType        { get; set; }
        public GuildRank     MinRankRequired { get; set; } = GuildRank.Member;
        public bool          IsBuilt         { get; set; } = true;
        public DateTime?     BuiltAt         { get; set; }
    }

    public class GuildHouseListing
    {
        public int           Id              { get; set; }
        public int           GuildPropertyId { get; set; }
        public GuildProperty GuildProperty   { get; set; } = null!;
        public int           SellerGuildId   { get; set; }
        public Guild         SellerGuild     { get; set; } = null!;
        public long          AskingPrice     { get; set; }
        public DateTime      ListedAt        { get; set; } = DateTime.UtcNow;
    }
}
