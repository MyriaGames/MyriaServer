using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastSaved = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Experience = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpForNextLvl = table.Column<long>(type: "INTEGER", nullable: false),
                    PotionTierAvailable = table.Column<int>(type: "INTEGER", nullable: false),
                    Class = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Race = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RaceSelected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentRoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastHealerRoomId = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentHealth = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentMana = table.Column<int>(type: "INTEGER", nullable: false),
                    StatStrength = table.Column<int>(type: "INTEGER", nullable: false),
                    StatDexterity = table.Column<int>(type: "INTEGER", nullable: false),
                    StatEndurance = table.Column<int>(type: "INTEGER", nullable: false),
                    StatIntelligence = table.Column<int>(type: "INTEGER", nullable: false),
                    StatSpirit = table.Column<int>(type: "INTEGER", nullable: false),
                    StatStrengthBonus = table.Column<int>(type: "INTEGER", nullable: false),
                    StatDexterityBonus = table.Column<int>(type: "INTEGER", nullable: false),
                    StatEnduranceBonus = table.Column<int>(type: "INTEGER", nullable: false),
                    StatIntelligenceBonus = table.Column<int>(type: "INTEGER", nullable: false),
                    StatSpiritBonus = table.Column<int>(type: "INTEGER", nullable: false),
                    StatUnusedPoints = table.Column<int>(type: "INTEGER", nullable: false),
                    StatBaseHealth = table.Column<int>(type: "INTEGER", nullable: false),
                    StatBaseMana = table.Column<int>(type: "INTEGER", nullable: false),
                    WeaponItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ArmorItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AccessoryItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MoneyBronze = table.Column<long>(type: "INTEGER", nullable: false),
                    MoneyCapacity = table.Column<long>(type: "INTEGER", nullable: false),
                    InventoryPages = table.Column<int>(type: "INTEGER", nullable: false),
                    LastClassPenaltyApplied = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastClassChanged = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActiveJobId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastJobChanged = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Characters_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Blocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BlockerCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Blocks_Characters_BlockedCharacterId",
                        column: x => x.BlockedCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Blocks_Characters_BlockerCharacterId",
                        column: x => x.BlockerCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CharacterActiveQuests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    KillProgressJson = table.Column<string>(type: "TEXT", nullable: false),
                    ItemProgressJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterActiveQuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterActiveQuests_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterClassXp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Class = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Xp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterClassXp", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterClassXp_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCombinedSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsStashed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StashedForClass = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCombinedSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCombinedSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCompletedQuests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCompletedQuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCompletedQuests_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCompositeSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsStashed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StashedForClass = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCompositeSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCompositeSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterInventoryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StackSize = table.Column<int>(type: "INTEGER", nullable: false),
                    SlotIndex = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterInventoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterInventoryItems_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SkillXp = table.Column<long>(type: "INTEGER", nullable: false),
                    KnowledgeXp = table.Column<long>(type: "INTEGER", nullable: false),
                    FameXp = table.Column<long>(type: "INTEGER", nullable: false),
                    LastFameTickDay = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSkillUsedDay = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterJobs_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterKnownRunes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseRuneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterKnownRunes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterKnownRunes_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRepeatableQuests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimesCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionsToday = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCompletionDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRepeatableQuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRepeatableQuests_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRoomGathering",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastGatheredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRoomGathering", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRoomGathering_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRuneDictionary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    WordId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CharacterLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsOfficiallyLearned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRuneDictionary", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRuneDictionary_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterSkillSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSkillSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterSkillSlots_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Friendships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequesterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddresseeCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Friendships_Characters_AddresseeCharacterId",
                        column: x => x.AddresseeCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Friendships_Characters_RequesterCharacterId",
                        column: x => x.RequesterCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Guilds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    LeaderCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Guilds_Characters_LeaderCharacterId",
                        column: x => x.LeaderCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCombinedSkillInputs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CombinedSkillId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCombinedSkillInputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCombinedSkillInputs_CharacterCombinedSkills_CombinedSkillId",
                        column: x => x.CombinedSkillId,
                        principalTable: "CharacterCombinedSkills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCompositeSkillComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompositeSkillId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCompositeSkillComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCompositeSkillComponents_CharacterCompositeSkills_CompositeSkillId",
                        column: x => x.CompositeSkillId,
                        principalTable: "CharacterCompositeSkills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterRuneAddedWords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KnownRuneId = table.Column<int>(type: "INTEGER", nullable: false),
                    WordId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterRuneAddedWords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterRuneAddedWords_CharacterKnownRunes_KnownRuneId",
                        column: x => x.KnownRuneId,
                        principalTable: "CharacterKnownRunes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicantCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildApplications_Characters_ApplicantCharacterId",
                        column: x => x.ApplicantCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildApplications_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildDeposits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildDeposits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildDeposits_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    InviterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InviteeCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRookieInvite = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildInvites_Characters_InviteeCharacterId",
                        column: x => x.InviteeCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildInvites_Characters_InviterCharacterId",
                        column: x => x.InviterCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildInvites_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildMembers",
                columns: table => new
                {
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMembers", x => new { x.GuildId, x.CharacterId });
                    table.ForeignKey(
                        name: "FK_GuildMembers_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildMembers_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildProperties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    CityRoomId = table.Column<int>(type: "INTEGER", nullable: true),
                    BaseAnchorRoomId = table.Column<int>(type: "INTEGER", nullable: true),
                    PricePaid = table.Column<long>(type: "INTEGER", nullable: true),
                    AcquiredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildProperties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildProperties_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildRookies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecruiterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    HiredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildRookies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildRookies_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildRookies_Characters_RecruiterCharacterId",
                        column: x => x.RecruiterCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuildRookies_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildSettings",
                columns: table => new
                {
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DepositWithdrawMinRank = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildSettings", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildSettings_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildTreasuries",
                columns: table => new
                {
                    GuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    Balance = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildTreasuries", x => x.GuildId);
                    table.ForeignKey(
                        name: "FK_GuildTreasuries_Guilds_GuildId",
                        column: x => x.GuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GuildHouseListings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildPropertyId = table.Column<int>(type: "INTEGER", nullable: false),
                    SellerGuildId = table.Column<int>(type: "INTEGER", nullable: false),
                    AskingPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    ListedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildHouseListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildHouseListings_GuildProperties_GuildPropertyId",
                        column: x => x.GuildPropertyId,
                        principalTable: "GuildProperties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuildHouseListings_Guilds_SellerGuildId",
                        column: x => x.SellerGuildId,
                        principalTable: "Guilds",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GuildRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildPropertyId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    RoomType = table.Column<int>(type: "INTEGER", nullable: false),
                    MinRankRequired = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBuilt = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildRooms_GuildProperties_GuildPropertyId",
                        column: x => x.GuildPropertyId,
                        principalTable: "GuildProperties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Blocks_BlockedCharacterId",
                table: "Blocks",
                column: "BlockedCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Blocks_BlockerCharacterId_BlockedCharacterId",
                table: "Blocks",
                columns: new[] { "BlockerCharacterId", "BlockedCharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterActiveQuests_CharacterId",
                table: "CharacterActiveQuests",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterClassXp_CharacterId",
                table: "CharacterClassXp",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCombinedSkillInputs_CombinedSkillId",
                table: "CharacterCombinedSkillInputs",
                column: "CombinedSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCombinedSkills_CharacterId",
                table: "CharacterCombinedSkills",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCompletedQuests_CharacterId",
                table: "CharacterCompletedQuests",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCompositeSkillComponents_CompositeSkillId",
                table: "CharacterCompositeSkillComponents",
                column: "CompositeSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCompositeSkills_CharacterId",
                table: "CharacterCompositeSkills",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterInventoryItems_CharacterId",
                table: "CharacterInventoryItems",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterJobs_CharacterId",
                table: "CharacterJobs",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterKnownRunes_CharacterId",
                table: "CharacterKnownRunes",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRepeatableQuests_CharacterId",
                table: "CharacterRepeatableQuests",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRoomGathering_CharacterId",
                table: "CharacterRoomGathering",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRuneAddedWords_KnownRuneId",
                table: "CharacterRuneAddedWords",
                column: "KnownRuneId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterRuneDictionary_CharacterId",
                table: "CharacterRuneDictionary",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_UserId_Name",
                table: "Characters",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharacterSkills_CharacterId",
                table: "CharacterSkills",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterSkillSlots_CharacterId",
                table: "CharacterSkillSlots",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_AddresseeCharacterId",
                table: "Friendships",
                column: "AddresseeCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_RequesterCharacterId_AddresseeCharacterId",
                table: "Friendships",
                columns: new[] { "RequesterCharacterId", "AddresseeCharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildApplications_ApplicantCharacterId",
                table: "GuildApplications",
                column: "ApplicantCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildApplications_GuildId_ApplicantCharacterId",
                table: "GuildApplications",
                columns: new[] { "GuildId", "ApplicantCharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildDeposits_GuildId_ItemId",
                table: "GuildDeposits",
                columns: new[] { "GuildId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildHouseListings_GuildPropertyId",
                table: "GuildHouseListings",
                column: "GuildPropertyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildHouseListings_SellerGuildId",
                table: "GuildHouseListings",
                column: "SellerGuildId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_GuildId_InviteeCharacterId",
                table: "GuildInvites",
                columns: new[] { "GuildId", "InviteeCharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_InviteeCharacterId",
                table: "GuildInvites",
                column: "InviteeCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildInvites_InviterCharacterId",
                table: "GuildInvites",
                column: "InviterCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildMembers_CharacterId",
                table: "GuildMembers",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildProperties_GuildId",
                table: "GuildProperties",
                column: "GuildId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildRookies_CharacterId",
                table: "GuildRookies",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildRookies_GuildId_CharacterId",
                table: "GuildRookies",
                columns: new[] { "GuildId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuildRookies_RecruiterCharacterId",
                table: "GuildRookies",
                column: "RecruiterCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildRooms_GuildPropertyId",
                table: "GuildRooms",
                column: "GuildPropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_LeaderCharacterId",
                table: "Guilds",
                column: "LeaderCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_Name",
                table: "Guilds",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Guilds_Tag",
                table: "Guilds",
                column: "Tag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blocks");

            migrationBuilder.DropTable(
                name: "CharacterActiveQuests");

            migrationBuilder.DropTable(
                name: "CharacterClassXp");

            migrationBuilder.DropTable(
                name: "CharacterCombinedSkillInputs");

            migrationBuilder.DropTable(
                name: "CharacterCompletedQuests");

            migrationBuilder.DropTable(
                name: "CharacterCompositeSkillComponents");

            migrationBuilder.DropTable(
                name: "CharacterInventoryItems");

            migrationBuilder.DropTable(
                name: "CharacterJobs");

            migrationBuilder.DropTable(
                name: "CharacterRepeatableQuests");

            migrationBuilder.DropTable(
                name: "CharacterRoomGathering");

            migrationBuilder.DropTable(
                name: "CharacterRuneAddedWords");

            migrationBuilder.DropTable(
                name: "CharacterRuneDictionary");

            migrationBuilder.DropTable(
                name: "CharacterSkills");

            migrationBuilder.DropTable(
                name: "CharacterSkillSlots");

            migrationBuilder.DropTable(
                name: "Friendships");

            migrationBuilder.DropTable(
                name: "GuildApplications");

            migrationBuilder.DropTable(
                name: "GuildDeposits");

            migrationBuilder.DropTable(
                name: "GuildHouseListings");

            migrationBuilder.DropTable(
                name: "GuildInvites");

            migrationBuilder.DropTable(
                name: "GuildMembers");

            migrationBuilder.DropTable(
                name: "GuildRookies");

            migrationBuilder.DropTable(
                name: "GuildRooms");

            migrationBuilder.DropTable(
                name: "GuildSettings");

            migrationBuilder.DropTable(
                name: "GuildTreasuries");

            migrationBuilder.DropTable(
                name: "CharacterCombinedSkills");

            migrationBuilder.DropTable(
                name: "CharacterCompositeSkills");

            migrationBuilder.DropTable(
                name: "CharacterKnownRunes");

            migrationBuilder.DropTable(
                name: "GuildProperties");

            migrationBuilder.DropTable(
                name: "Guilds");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
