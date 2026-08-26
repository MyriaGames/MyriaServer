using Microsoft.EntityFrameworkCore;
using Myria.Server.Realm.Models;

namespace Myria.Server.Realm.Data
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        // ── Account tables ───────────────────────────────────────────────────────
        // Users live in a separate MyriaAuthServer database — Character.UserId is a
        // plain string column (the auth service's User.Id, from the JWT sub claim),
        // not an EF FK/navigation into this context.
        public DbSet<Character>  Characters  => Set<Character>();
        public DbSet<Friendship> Friendships => Set<Friendship>();
        public DbSet<Block>      Blocks      => Set<Block>();

        // ── Guild tables ─────────────────────────────────────────────────────────
        public DbSet<Guild>             Guilds             => Set<Guild>();
        public DbSet<GuildSettings>     GuildSettings      => Set<GuildSettings>();
        public DbSet<GuildTreasury>     GuildTreasuries    => Set<GuildTreasury>();
        public DbSet<GuildDeposit>      GuildDeposits      => Set<GuildDeposit>();
        public DbSet<GuildMember>       GuildMembers       => Set<GuildMember>();
        public DbSet<GuildRookie>       GuildRookies       => Set<GuildRookie>();
        public DbSet<GuildInvite>       GuildInvites       => Set<GuildInvite>();
        public DbSet<GuildApplication>  GuildApplications  => Set<GuildApplication>();
        public DbSet<GuildProperty>     GuildProperties    => Set<GuildProperty>();
        public DbSet<GuildRoom>         GuildRooms         => Set<GuildRoom>();
        public DbSet<GuildHouseListing> GuildHouseListings => Set<GuildHouseListing>();

        // ── Player shops ─────────────────────────────────────────────────────────
        public DbSet<PlayerShop>     PlayerShops     => Set<PlayerShop>();
        public DbSet<PlayerShopItem> PlayerShopItems => Set<PlayerShopItem>();

        // ── Character child tables ───────────────────────────────────────────────
        public DbSet<CharacterInventoryItem>       CharacterInventoryItems      => Set<CharacterInventoryItem>();
        public DbSet<CharacterSkill>               CharacterSkills              => Set<CharacterSkill>();
        public DbSet<CharacterActiveQuest>         CharacterActiveQuests        => Set<CharacterActiveQuest>();
        public DbSet<CharacterCompletedQuest>      CharacterCompletedQuests     => Set<CharacterCompletedQuest>();
        public DbSet<CharacterRepeatableQuest>     CharacterRepeatableQuests    => Set<CharacterRepeatableQuest>();
        public DbSet<CharacterJob>                 CharacterJobs                => Set<CharacterJob>();
        public DbSet<CharacterSkillSlot>           CharacterSkillSlots          => Set<CharacterSkillSlot>();
        public DbSet<CharacterCompositeSkill>      CharacterCompositeSkills     => Set<CharacterCompositeSkill>();
        public DbSet<CharacterCompositeSkillComponent> CharacterCompositeSkillComponents => Set<CharacterCompositeSkillComponent>();
        public DbSet<CharacterCombinedSkill>       CharacterCombinedSkills      => Set<CharacterCombinedSkill>();
        public DbSet<CharacterCombinedSkillInput>  CharacterCombinedSkillInputs => Set<CharacterCombinedSkillInput>();
        public DbSet<CharacterKnownRune>           CharacterKnownRunes          => Set<CharacterKnownRune>();
        public DbSet<CharacterRuneAddedWord>       CharacterRuneAddedWords      => Set<CharacterRuneAddedWord>();
        public DbSet<CharacterRuneDictEntry>       CharacterRuneDictionary      => Set<CharacterRuneDictEntry>();
        public DbSet<CharacterRoomGathering>       CharacterRoomGathering       => Set<CharacterRoomGathering>();
        public DbSet<CharacterClassXp>             CharacterClassXp             => Set<CharacterClassXp>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── Characters ───────────────────────────────────────────────────────
            modelBuilder.Entity<Character>(e =>
            {
                e.Property(c => c.UserId).HasMaxLength(50);
                e.HasIndex(c => new { c.UserId, c.Name }).IsUnique();
                e.Property(c => c.Name).HasMaxLength(50);
            });

            // ── Character children — all cascade-delete when Character is removed ─
            // Item/skill/quest/job/room references on these are plain unconstrained
            // string/int IDs, validated against the JSON game-content catalog (same
            // as the client) rather than enforced as DB foreign keys.
            modelBuilder.Entity<CharacterInventoryItem>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.InventoryItems)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterSkill>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.Skills)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterActiveQuest>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.ActiveQuests)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterCompletedQuest>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.CompletedQuests)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterRepeatableQuest>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.RepeatableQuests)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterJob>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.Jobs)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterSkillSlot>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.SkillSlots)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterCompositeSkill>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.CompositeSkills)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterCompositeSkillComponent>(e =>
                e.HasOne(x => x.CompositeSkill).WithMany(cs => cs.Components)
                    .HasForeignKey(x => x.CompositeSkillId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterCombinedSkill>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.CombinedSkills)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterCombinedSkillInput>(e =>
                e.HasOne(x => x.CombinedSkill).WithMany(cs => cs.Inputs)
                    .HasForeignKey(x => x.CombinedSkillId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterKnownRune>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.KnownRunes)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterRuneAddedWord>(e =>
                e.HasOne(x => x.KnownRune).WithMany(r => r.AddedWords)
                    .HasForeignKey(x => x.KnownRuneId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterRuneDictEntry>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.RuneDictionary)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterRoomGathering>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.RoomGatheringStatus)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<CharacterClassXp>(e =>
                e.HasOne(x => x.Character).WithMany(c => c.ClassXp)
                    .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade));

            // ── Friendships ───────────────────────────────────────────────────────
            modelBuilder.Entity<Friendship>(e =>
            {
                e.HasOne(f => f.RequesterCharacter).WithMany().HasForeignKey(f => f.RequesterCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(f => f.AddresseeCharacter).WithMany().HasForeignKey(f => f.AddresseeCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(f => new { f.RequesterCharacterId, f.AddresseeCharacterId }).IsUnique();
            });

            modelBuilder.Entity<Block>(e =>
            {
                e.HasOne(b => b.BlockerCharacter).WithMany().HasForeignKey(b => b.BlockerCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(b => b.BlockedCharacter).WithMany().HasForeignKey(b => b.BlockedCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(b => new { b.BlockerCharacterId, b.BlockedCharacterId }).IsUnique();
            });

            // ── Guilds ────────────────────────────────────────────────────────────
            modelBuilder.Entity<Guild>(e =>
            {
                e.HasIndex(g => g.Name).IsUnique();
                e.HasIndex(g => g.Tag).IsUnique();
                e.HasOne(g => g.LeaderCharacter).WithMany()
                    .HasForeignKey(g => g.LeaderCharacterId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GuildSettings>(e =>
            {
                e.HasKey(s => s.GuildId);
                e.HasOne(s => s.Guild).WithOne(g => g.Settings)
                    .HasForeignKey<GuildSettings>(s => s.GuildId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GuildTreasury>(e =>
            {
                e.HasKey(t => t.GuildId);
                e.HasOne(t => t.Guild).WithOne(g => g.Treasury)
                    .HasForeignKey<GuildTreasury>(t => t.GuildId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GuildDeposit>(e =>
            {
                e.HasOne(d => d.Guild).WithMany(g => g.Deposit)
                    .HasForeignKey(d => d.GuildId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(d => new { d.GuildId, d.ItemId }).IsUnique();
            });

            modelBuilder.Entity<GuildMember>(e =>
            {
                e.HasKey(m => new { m.GuildId, m.CharacterId });
                e.HasOne(m => m.Guild).WithMany(g => g.Members)
                    .HasForeignKey(m => m.GuildId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(m => m.Character).WithMany()
                    .HasForeignKey(m => m.CharacterId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GuildRookie>(e =>
            {
                e.HasOne(r => r.Guild).WithMany(g => g.Rookies)
                    .HasForeignKey(r => r.GuildId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(r => r.Character).WithMany()
                    .HasForeignKey(r => r.CharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(r => r.RecruiterCharacter).WithMany()
                    .HasForeignKey(r => r.RecruiterCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(r => new { r.GuildId, r.CharacterId }).IsUnique();
            });

            modelBuilder.Entity<GuildInvite>(e =>
            {
                e.HasOne(i => i.Guild).WithMany(g => g.Invites)
                    .HasForeignKey(i => i.GuildId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(i => i.InviterCharacter).WithMany()
                    .HasForeignKey(i => i.InviterCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(i => i.InviteeCharacter).WithMany()
                    .HasForeignKey(i => i.InviteeCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(i => new { i.GuildId, i.InviteeCharacterId }).IsUnique();
            });

            modelBuilder.Entity<GuildApplication>(e =>
            {
                e.HasOne(a => a.Guild).WithMany(g => g.Applications)
                    .HasForeignKey(a => a.GuildId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(a => a.ApplicantCharacter).WithMany()
                    .HasForeignKey(a => a.ApplicantCharacterId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(a => new { a.GuildId, a.ApplicantCharacterId }).IsUnique();
            });

            modelBuilder.Entity<GuildProperty>(e =>
                e.HasOne(p => p.Guild).WithOne(g => g.Property)
                    .HasForeignKey<GuildProperty>(p => p.GuildId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<GuildRoom>(e =>
                e.HasOne(r => r.GuildProperty).WithMany(p => p.Rooms)
                    .HasForeignKey(r => r.GuildPropertyId).OnDelete(DeleteBehavior.Cascade));

            modelBuilder.Entity<GuildHouseListing>(e =>
            {
                e.HasOne(l => l.GuildProperty).WithOne(p => p.Listing)
                    .HasForeignKey<GuildHouseListing>(l => l.GuildPropertyId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(l => l.SellerGuild).WithMany()
                    .HasForeignKey(l => l.SellerGuildId).OnDelete(DeleteBehavior.NoAction);
            });

            // ── Player shops ──────────────────────────────────────────────────────
            modelBuilder.Entity<PlayerShop>(e =>
            {
                e.HasIndex(s => s.OwnerCharacterId).IsUnique(); // one open shop per character
                e.HasOne(s => s.OwnerCharacter).WithMany()
                    .HasForeignKey(s => s.OwnerCharacterId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<PlayerShopItem>(e =>
            {
                e.HasOne(i => i.PlayerShop).WithMany(s => s.Items)
                    .HasForeignKey(i => i.PlayerShopId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(i => new { i.PlayerShopId, i.ItemId }).IsUnique();
            });
        }
    }
}
