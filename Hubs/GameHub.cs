using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Repositories;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Services.Regestries;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.Mods;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Services;

namespace Myria.Server.Realm.Hubs
{
    [Authorize]
    public class GameHub(
        CharacterPresenceService presence,
        PartyService party,
        CharacterSessionService session,
        TradeService trade,
        PlayerShopService shop,
        GroupCombatService groupCombat,
        PvpCombatService pvp,
        WorldStateService world,
        IServiceScopeFactory scopeFactory,
        AllowedVersionsConfig allowedVersions,
        ILogger<GameHub> logger,
        CharacterLocks locks
    ) : Hub
    {
        /// <summary>
        /// Saves a character with a few retries on transient failure (e.g. SQLite "database is
        /// locked" under concurrent writes) instead of giving up silently after one attempt.
        /// Logs when every attempt fails, so a real persistence problem is visible instead of
        /// vanishing into a swallowed exception - this is what actually commits combat XP/level
        /// gains to disk, so a silent failure here is how "I leveled up but it didn't save" bugs
        /// happen.
        /// </summary>
        private async Task<bool> SaveCharacterReliablyAsync(
            ICharacterRepository repo, string username, Myria.Lib.Core.Entities.Characters.Character player, string context)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await repo.SaveAsync(username, player);
                    return true;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    logger.LogWarning(ex,
                        "Character save failed ({Context}), attempt {Attempt}/{MaxAttempts} for {Character}",
                        context, attempt, maxAttempts, player.Name);
                    await Task.Delay(150 * attempt);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Character save permanently failed ({Context}) for {Character} after {MaxAttempts} attempts",
                        context, player.Name, maxAttempts);
                    return false;
                }
            }
            return false;
        }

        // ── Connection lifecycle ──────────────────────────────────────────────

        public override async Task OnConnectedAsync()
        {
            var username = Context.User!.Identity!.Name!;

            // Exact-version gate — deliberately stricter than the client's own auto-updater (which
            // treats same-compat-tier differences as safe to apply silently): once connected, this
            // realm wants to know the client is running the literal same released build, not just a
            // compatible one. Only this realm enforces this; MyriaAuthServer deliberately does not,
            // so a player on a rejected client version can still log in and manage their account.
            var clientVersion = Context.GetHttpContext()?.Request.Query["clientVersion"].ToString();
            if (!allowedVersions.IsAllowed(clientVersion))
            {
                logger.LogInformation(
                    "{User} rejected at connect: client version '{ClientVersion}' not in allowed list (conn {Conn})",
                    username, clientVersion, Context.ConnectionId);
                await Clients.Caller.SendAsync("VersionMismatch", allowedVersions.AllowedClientVersions);
                Context.Abort();
                return;
            }

            // Operator account ban (admin site) - realm-only: the account can still log in to the
            // auth service and manage itself, it just can't play here. Checked before this
            // connection is registered anywhere, so a banned account never appears online.
            using (var banScope = scopeFactory.CreateScope())
            {
                var bans = banScope.ServiceProvider.GetRequiredService<BanService>();
                if (await bans.GetActiveAccountBanAsync(username) is { } accountBan)
                {
                    logger.LogInformation("{User} rejected at connect: account banned until {Until} (conn {Conn})",
                        username, accountBan.Until, Context.ConnectionId);
                    await Clients.Caller.SendAsync("AccountBanned",
                        Bans.IsPermanent(accountBan.Until) ? (DateTime?)null : accountBan.Until, accountBan.Reason);
                    Context.Abort();
                    return;
                }
            }

            var oldConn = presence.GetConnectionByAccount(username);
            if (oldConn != null)
            {
                logger.LogInformation("{User} reconnected (conn {Conn}); forcing logout of stale connection {OldConn}",
                    username, Context.ConnectionId, oldConn);
                await Clients.Client(oldConn).SendAsync("ForceLogout");
                presence.GetContext(oldConn)?.Abort();
            }
            else
            {
                logger.LogInformation("{User} connected (conn {Conn})", username, Context.ConnectionId);
            }

            presence.Connect(Context.ConnectionId, username);
            presence.RegisterContext(Context.ConnectionId, Context);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            presence.UnregisterContext(Context.ConnectionId);

            // A disconnect mid-PvP is a forfeit. Must run before the session is removed and saved
            // below: it settles the loss and restores pre-fight HP/MP on the session character, so
            // the disconnect save persists exactly that.
            await ForfeitPvpAsync(Context.ConnectionId, session.Get(Context.ConnectionId), notifyCaller: false);

            var displayName = presence.GetDisplayName(Context.ConnectionId);
            var roomId      = presence.GetRoom(Context.ConnectionId);

            // Auto-save session player and notify guild of offline status
            var player   = session.Remove(Context.ConnectionId);
            var username = Context.User?.Identity?.Name;

            logger.LogInformation("{User} disconnected (conn {Conn}, character {Character})",
                username, Context.ConnectionId, player?.Name ?? "none");
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                if (player != null && username != null)
                {
                    var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
                    await SaveCharacterReliablyAsync(repo, username, player, "disconnect");
                }

                if (displayName is not null)
                {
                    var charId = await GetCharacterIdByNameAsync(db, displayName);
                    if (charId != 0)
                    {
                        var guildId = await GetGuildIdForCharacterAsync(db, charId);
                        if (guildId is not null)
                        {
                            await Clients.Group(GuildService.GuildGroup(guildId.Value))
                                .SendAsync("GuildMemberOffline", displayName);
                            await Groups.RemoveFromGroupAsync(
                                Context.ConnectionId, GuildService.GuildGroup(guildId.Value));
                        }
                    }
                }
            }

            presence.Disconnect(Context.ConnectionId);

            if (roomId is int rid && displayName is not null)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(rid));
                await Clients.Group(RoomGroup(rid)).SendAsync("CharacterLeft", displayName);
            }

            if (displayName is not null)
                await HandlePartyLeave(displayName, notifyCaller: false);

            await CancelTradeInternal("partner_offline");

            // Going offline always hides the shop (regardless of the Merchant's Seal - that only
            // keeps it visible while online but physically elsewhere), but the shop itself, its
            // storage and listings are NOT deleted - GetEffectiveState just returns false the
            // moment presence.IsCharacterOnline is false, and it reappears automatically next
            // login without the player needing to reopen anything.
            if (displayName is not null)
            {
                using var shopScope = scopeFactory.CreateScope();
                var shopSvc = shopScope.ServiceProvider.GetRequiredService<PlayerShopService>();
                var ownShop = await shopSvc.GetShopByOwnerNameAsync(displayName);
                if (ownShop is not null)
                    await Clients.Group(RoomGroup(ownShop.RoomId)).SendAsync("ShopClosed", displayName);
            }

            var gcPartyId = groupCombat.RemoveConn(Context.ConnectionId);
            if (gcPartyId is not null)
            {
                var abortSnap = EmptyGroupSnapshot() with { Finished = true };
                await Clients.Group(PartyService.PartyGroup(gcPartyId))
                    .SendAsync("GroupCombatFinished", abortSnap);
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ── Session ───────────────────────────────────────────────────────────

        // The presence name is what DisplayName() - and through it ~45 hub methods (guild, shop,
        // trade, party, chat) - treat as "who is calling", and what GetConnectionByName routes
        // whispers/invites to. It must therefore only ever be the character this connection has
        // loaded via LoadCharacter (which verifies ownership and sets the name itself). Accepting
        // an arbitrary name here let any player act as any other character (security review
        // 2026-11-25, A01 F1). Kept as a method because clients call it after LoadCharacter; a
        // name other than the loaded character's is ignored.
        public Task SetCharacterName(string characterName)
        {
            var loaded = session.Get(Context.ConnectionId);
            if (loaded is null ||
                !string.Equals(loaded.Name, characterName?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("{User} tried to set character name {Requested} on conn {Conn} but has {Loaded} loaded - ignored",
                    Context.User?.Identity?.Name, LogSafe.Text(characterName), Context.ConnectionId, loaded?.Name ?? "nothing");
                return Task.CompletedTask;
            }

            presence.SetCharacterName(Context.ConnectionId, loaded.Name);
            return NotifyShopReopenIfSealedAsync(loaded.Name);
        }

        // "The shop reopens automatically on login" - the DB row/storage never went away on
        // disconnect, so this just needs to (re-)announce visibility now that the owner is online
        // again. Called from SetCharacterName rather than OnConnectedAsync because the character
        // name isn't known yet at connect time (presence.Connect stores it empty until this call).
        // If the shop is only sealed (not physically present), it's visible right away; if not
        // sealed, JoinRoom announces it once they actually walk back into that room - there's no
        // "current room" yet to check against until then.
        private async Task NotifyShopReopenIfSealedAsync(string? characterName)
        {
            if (string.IsNullOrEmpty(characterName)) return;

            using var shopScope = scopeFactory.CreateScope();
            var shopSvc = shopScope.ServiceProvider.GetRequiredService<PlayerShopService>();
            var ownShop = await shopSvc.GetShopByOwnerNameAsync(characterName);
            if (ownShop is not null && PlayerShopService.HasSeal(ownShop))
                await Clients.Group(RoomGroup(ownShop.RoomId)).SendAsync("ShopOpened", characterName);
        }

        /// <summary>
        /// Receives the client's active mod list and validates it for multiplayer.
        /// Gameplay-affecting mods are flagged; the client must reload vanilla data before playing.
        /// Visual-only mods are always accepted.
        /// Responds with a "ModValidation" event: (bool accepted, string message).
        /// </summary>
        public async Task ReportMods(ModInfo modInfo)
        {
            bool accepted = !modInfo.HasGameplayMods;
            string message = accepted
                ? modInfo.ActiveMods.Count == 0
                    ? "No mods loaded — vanilla client."
                    : $"Visual mod(s) only ({modInfo.ActiveMods.Count} loaded) — accepted."
                : $"Gameplay mod(s) detected ({modInfo.ActiveMods.Count(m => !m.IsVisualOnly)} mod(s)). They are disabled for multiplayer; client must use vanilla data.";

            await Clients.Caller.SendAsync("ModValidation", accepted, message);
        }

        /// <summary>
        /// Loads the named character into the server session.
        /// Must be called once after connecting before any game actions are valid.
        /// </summary>
        public async Task<bool> LoadCharacter(string characterName)
        {
            var username = Context.User!.Identity!.Name!;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Verify the caller actually owns this character before touching any live session.
            // CharacterSessionService.TryReattach (and GroupCombatService.ReassignConnection,
            // called below) match purely by character name with no notion of ownership - the
            // in-memory Character has no UserId - so without this check any authenticated player
            // who knows another player's online character name could reattach that character's
            // live session (inventory/gold/gear) onto their own connection.
            var owns = await db.Characters.AnyAsync(c => c.UserId == username && c.Name == characterName);
            if (!owns)
            {
                // Trying to take over another player's character (the A01 F1 attack class) used to leave no trace
                // (security review 2026-11-26, A09 F2).
                await SecurityLog.NoteForeignCharacterAsync(db, logger, username, characterName, "hub:LoadCharacter");
                return false;
            }

            // Per-character operator ban. Must come before TryReattach below, or a banned character
            // whose session is still live could just be picked back up.
            if (await scope.ServiceProvider.GetRequiredService<BanService>()
                    .GetActiveCharacterBanAsync(username, characterName) is { } characterBan)
            {
                await Clients.Caller.SendAsync("CharacterBanned", characterName,
                    Bans.IsPermanent(characterBan.Until) ? (DateTime?)null : characterBan.Until, characterBan.Reason);
                return false;
            }

            // Prefer reattaching an already-live session for this character (see
            // CharacterSessionService.TryReattach) over a fresh DB read - a reload here would
            // silently discard any progress made in memory since the last save.
            var reattached = session.TryReattach(characterName, Context.ConnectionId);
            if (reattached is null)
            {
                var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
                // Switching from another character on this connection: save it first. It used to be
                // dropped unsaved while its trades/shop deposits had already reached the other side or
                // the database - switch away and back, and the item existed twice (A06 F6).
                if (session.Get(Context.ConnectionId) is { } previous)
                {
                    using (await locks.AcquireAsync(previous.Name))
                        await SaveCharacterReliablyAsync(repo, username, previous, "character-switch");
                }

                var player = await repo.LoadAsync(username, characterName);
                if (player == null) return false;

                session.Remove(Context.ConnectionId);
                if (!session.TryAdd(Context.ConnectionId, player)) return false;

                logger.LogInformation(
                    "{Character} loaded from DB onto conn {Conn} (Level {Level})",
                    characterName, Context.ConnectionId, player.Level);
            }
            else
            {
                logger.LogInformation(
                    "{Character} reattached to a live session on conn {Conn} (Level {Level}) - reconnect during play",
                    characterName, Context.ConnectionId, reattached.Level);
            }

            // Ownership is verified above, so this is the one place the connection's in-game name
            // is set (see SetCharacterName). Uses the loaded character's own spelling, not the
            // request's, since character names match case-insensitively.
            presence.SetCharacterName(Context.ConnectionId, session.Get(Context.ConnectionId)!.Name);

            // Join guild SignalR group if the character belongs to one
            var charId = await GetCharacterIdByNameAsync(db, characterName);
            if (charId != 0)
            {
                var guildId = await GetGuildIdForCharacterAsync(db, charId);
                if (guildId is not null)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GuildService.GuildGroup(guildId.Value));
                    var rank = await db.GuildMembers
                        .Where(m => m.CharacterId == charId)
                        .Select(m => (GuildRank?)m.Rank)
                        .FirstOrDefaultAsync();
                    await Clients.OthersInGroup(GuildService.GuildGroup(guildId.Value))
                        .SendAsync("GuildMemberOnline", characterName, rank?.ToString() ?? "Rookie");
                }
            }

            // Join party SignalR group if already in one. A fresh connection (e.g. after a
            // reconnect) is never automatically re-added to its party's group, so without this
            // it silently stops receiving party/combat broadcasts (GroupCombatStarted etc.) even
            // though PartyService still correctly considers it a member - the party overlay stays
            // visible client-side while "Group Fight" quietly starts a second, disconnected
            // encounter for this player instead of joining the real one.
            var partyId = party.GetPartyId(characterName);
            if (partyId is not null)
                await Groups.AddToGroupAsync(Context.ConnectionId, PartyService.PartyGroup(partyId));

            // Same reconnect gap as above, but for group combat specifically: a fresh connection
            // ID is never automatically re-associated with an in-progress GroupCombatEncounter,
            // so re-point it if this character is mid-fight - otherwise their turn silently
            // no-ops forever (see GroupCombatService.ReassignConnection for the full story).
            groupCombat.ReassignConnection(characterName, Context.ConnectionId);

            // Re-pointing the connection above is invisible to the client - nothing tells it a
            // fight is even happening, so a client that reconnected mid-fight (or simply loaded
            // this page late) would never navigate into the fight UI at all, let alone know whose
            // turn it is. Re-sending "GroupCombatStarted" (same message the fight page already
            // knows how to react to - see ViewModel_PageGame.OnGroupCombatStarted) to just this
            // caller re-triggers that navigation/init with the encounter's current live state.
            if (groupCombat.GetPartyId(Context.ConnectionId) is string rejoinPartyId &&
                groupCombat.GetEncounter(rejoinPartyId) is { } rejoinEncounter)
            {
                await Clients.Caller.SendAsync("GroupCombatStarted", new StartGroupCombatResult(
                    true, null,
                    BuildCharacterStates(rejoinEncounter),
                    BuildMonsterStates(rejoinEncounter),
                    rejoinEncounter.CurrentTurnCharacterName));
            }

            return true;
        }

        /// <summary>Saves the current session state to the database immediately.</summary>
        public async Task<bool> SaveSession()
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

            var username = Context.User!.Identity!.Name!;
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            return await SaveCharacterReliablyAsync(repo, username, player, "save-session");
        }

        /// <summary>Full HP/mana restore at a Healer NPC. The server holds the authoritative
        /// character for this session, so this - not any client-local mutation - is what actually
        /// counts; the client applies the returned values via SetHealth/SetMana to refresh its UI.</summary>
        public async Task<HealActionResult> Heal()
        {
            var player = session.Get(Context.ConnectionId);
            if (player is null)
                return new HealActionResult(false, "Not in session", 0, 0, 0, 0);

            // Only at a healer, and never mid-fight: this used to work anywhere, any time - a free
            // full restore during combat made dying practically impossible (A06 F8).
            var roomId = presence.GetRoom(Context.ConnectionId);
            var room = roomId is null ? null : RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId.Value);
            if (room is null || !room.NpcRefs.Any(n => n?.Type == NpcType.Healer))
                return new HealActionResult(false, "no_healer_here", player.CurrentHealth, player.MaxHealth, player.CurrentMana, player.MaxMana);
            if (session.GetEncounter(Context.ConnectionId) is not null || groupCombat.GetPartyId(Context.ConnectionId) is not null)
                return new HealActionResult(false, "in_combat", player.CurrentHealth, player.MaxHealth, player.CurrentMana, player.MaxMana);

            player.Heal(int.MaxValue, "Healer");
            player.RestoreMana(int.MaxValue, "Healer");

            logger.LogInformation("{Character} healed to full (conn {Conn})", player.Name, Context.ConnectionId);

            var username = Context.User!.Identity!.Name!;
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            await SaveCharacterReliablyAsync(repo, username, player, "heal");

            await NotifyCharacterUpdate(player, vitals: true);
            return new HealActionResult(true, null, player.CurrentHealth, player.MaxHealth, player.CurrentMana, player.MaxMana);
        }

        /// <summary>
        /// Authoritative Level/Experience/stat-progression snapshot — the client calls this
        /// after any action that might have granted XP (combat win, etc.) instead of trying to
        /// replay the server's GainXp()/LevelUp() math locally, which drifts.
        /// </summary>
        public Task<CharacterProgressResult?> GetCharacterProgress()
        {
            var player = session.Get(Context.ConnectionId);
            if (player is null)
                return Task.FromResult<CharacterProgressResult?>(null);

            return Task.FromResult<CharacterProgressResult?>(BuildProgress(player));
        }

        private static CharacterProgressResult BuildProgress(Myria.Lib.Core.Entities.Characters.Character player) =>
            new(
                player.Level,
                player.Experience,
                player.ExpForNextLvl,
                player.Stats.Strength,
                player.Stats.Dexterity,
                player.Stats.Endurance,
                player.Stats.Intelligence,
                player.Stats.Spirit,
                player.Stats.UnusedPoints,
                player.Stats.BaseHealth,
                player.Stats.BaseMana);

        /// <summary>
        /// Pushes a <see cref="CharacterUpdateDto"/> to the caller covering whichever sections
        /// changed - the single generalized replacement for hand-mirroring a server-side
        /// mutation onto the client's local Character at every call site (see PlayerShop's
        /// deposit/withdraw for the pattern this replaces). Combat keeps its own dedicated result
        /// DTOs (CombatTurnResult / GroupCombatSnapshot) and doesn't need this.
        /// </summary>
        private Task NotifyCharacterUpdate(
            Myria.Lib.Core.Entities.Characters.Character player,
            bool inventory = false, bool money = false, bool vitals = false, bool progress = false,
            bool questProgress = false, bool jobs = false, bool runes = false, bool equipment = false,
            bool skillProgress = false, bool pvpStats = false)
        {
            var dto = BuildCharacterUpdate(player, inventory, money, vitals, progress, questProgress, jobs, runes, equipment, skillProgress, pvpStats);
            return dto is null ? Task.CompletedTask : Clients.Caller.SendAsync("CharacterUpdated", dto);
        }

        /// <summary>Same as <see cref="NotifyCharacterUpdate"/>, but for a connection other than
        /// the caller (e.g. the other side of a completed trade).</summary>
        private Task NotifyCharacterUpdateFor(string connectionId, Myria.Lib.Core.Entities.Characters.Character player,
            bool inventory = false, bool money = false, bool vitals = false, bool progress = false,
            bool questProgress = false, bool jobs = false, bool runes = false, bool equipment = false,
            bool skillProgress = false, bool pvpStats = false)
        {
            var dto = BuildCharacterUpdate(player, inventory, money, vitals, progress, questProgress, jobs, runes, equipment, skillProgress, pvpStats);
            return dto is null ? Task.CompletedTask : Clients.Client(connectionId).SendAsync("CharacterUpdated", dto);
        }

        private static CharacterUpdateDto? BuildCharacterUpdate(
            Myria.Lib.Core.Entities.Characters.Character player,
            bool inventory, bool money, bool vitals, bool progress, bool questProgress = false,
            bool jobs = false, bool runes = false, bool equipment = false, bool skillProgress = false, bool pvpStats = false)
        {
            if (!inventory && !money && !vitals && !progress && !questProgress && !jobs && !runes && !equipment && !skillProgress && !pvpStats) return null;

            return new CharacterUpdateDto(
                inventory ? player.Inventory.Items.Select(i => new InventoryItemSnapshot(i.Id, i.StackSize)).ToList() : null,
                money ? player.Money.Balance.BronzeTotal : null,
                vitals ? player.CurrentHealth : null,
                vitals ? player.MaxHealth : null,
                vitals ? player.CurrentMana : null,
                vitals ? player.MaxMana : null,
                progress ? BuildProgress(player) : null,
                questProgress ? BuildQuestProgress(player) : null,
                jobs ? player.Jobs.Select(j => new JobProgressSnapshot(j.JobId, j.SkillXp, j.KnowledgeXp, j.FameXp)).ToList() : null,
                runes ? player.KnownRunes.Select(r => new RuneSnapshot(r.Id, r.BaseRuneId, new List<string>(r.AddedWordIds))).ToList() : null,
                equipment ? new EquippedSnapshot(player.WeaponSlot?.Id, player.ArmorSlot?.Id, player.AccessorySlot?.Id) : null,
                skillProgress ? player.SkillProgress.Select(sp => new SkillProgressSnapshot(
                    sp.SkillId, sp.UsageCount, sp.Level, sp.UnspentPoints, new List<string>(sp.PurchasedUpgradeIds))).ToList() : null,
                pvpStats ? new PvpStatsSnapshot(player.PvpRating, player.PvpCurrency, player.PvpWins, player.PvpLosses) : null);
        }

        /// <summary>
        /// Authoritative kill/item objective progress for every currently active quest — the
        /// client calls this after combat so quests being progressed mid-fight (server-authoritative
        /// combat only ticks QuestManager's counters against this session's own Character) don't
        /// stay frozen client-side until the next full character reload.
        /// </summary>
        public Task<List<QuestProgressState>> GetActiveQuestProgress()
        {
            var player = session.Get(Context.ConnectionId);
            if (player is null)
                return Task.FromResult(new List<QuestProgressState>());

            return Task.FromResult(BuildQuestProgress(player));
        }

        private static List<QuestProgressState> BuildQuestProgress(Myria.Lib.Core.Entities.Characters.Character player)
        {
            // Item counters from the current inventory (sold/used items count down), so the client shows
            // and decides on what the hand-in will actually check.
            QuestManager.UpdateItemProgress(player);
            return player.ActiveQuests.Select(q => new QuestProgressState(
                q.Id,
                new Dictionary<int, int>(q.KillProgress),
                new Dictionary<string, int>(q.ItemProgress))).ToList();
        }

        // ── Room presence ─────────────────────────────────────────────────────

        public async Task<bool> JoinRoom(int roomId)
        {
            var displayName = DisplayName();
            var player      = session.Get(Context.ConnectionId);
            var prevRoom    = presence.GetRoom(Context.ConnectionId);

            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId);
            if (room is null) return false;

            if (player is not null)
            {
                bool allowed = room.RequirementType switch
                {
                    RoomRequirementType.Level => player.Level >= room.AccessLevel,
                    RoomRequirementType.Quest => !string.IsNullOrEmpty(room.RequiredQuestId) &&
                        player.CompletedQuests.Any(q =>
                            string.Equals(q.Id, room.RequiredQuestId, StringComparison.OrdinalIgnoreCase)),
                    RoomRequirementType.Party => party.GetPartyId(displayName) is not null,
                    _                         => true
                };

                if (!allowed) return false;

                player.CurrentRoomId = roomId;
                player.CurrentRoom   = room;

                if (room.NpcRefs.Any(n => n?.Type == NpcType.Healer))
                    player.LastHealerRoomId = roomId;
            }

            if (prevRoom is int prev && prev != roomId)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(prev));
                await Clients.OthersInGroup(RoomGroup(prev)).SendAsync("CharacterLeft", displayName);
            }

            presence.SetRoom(Context.ConnectionId, roomId);
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));

            var others = presence.GetCharactersInRoom(roomId)
                                 .Where(u => u != displayName)
                                 .ToList();
            await Clients.Caller.SendAsync("RoomCharacters", others);
            await Clients.OthersInGroup(RoomGroup(roomId)).SendAsync("CharacterEntered", displayName);

            await HandleShopRoomTransitionAsync(displayName, prevRoom, roomId);
            return true;
        }

        // Walking out of/back into a room can flip a shop's visibility even though nothing about
        // the shop itself changed - see PlayerShopService.GetEffectiveState. A shop kept open by
        // a Merchant's Seal is unaffected either way (it's visible in its room regardless of
        // where the owner currently stands), so this only matters for a shop with no seal.
        private async Task HandleShopRoomTransitionAsync(string displayName, int? prevRoom, int newRoom)
        {
            var ownShop = await shop.GetShopByOwnerNameAsync(displayName);
            if (ownShop is not null)
            {
                if (prevRoom is int prev && prev == ownShop.RoomId && prev != newRoom && !PlayerShopService.HasSeal(ownShop))
                    await Clients.Group(RoomGroup(prev)).SendAsync("ShopClosed", displayName);

                if (ownShop.RoomId == newRoom)
                    await Clients.Group(RoomGroup(newRoom)).SendAsync("ShopOpened", displayName);
            }

            var visibleOwners = await shop.GetVisibleShopOwnerNamesInRoomAsync(newRoom);
            await Clients.Caller.SendAsync("RoomShops", visibleOwners);
        }

        // ── Chat ──────────────────────────────────────────────────────────────

        private const int MaxMessageLength = 500;

        public async Task SendMessage(string message, string channel, string? target = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (message.Length > MaxMessageLength) return;

            var displayName = DisplayName();
            var roomId      = presence.GetRoom(Context.ConnectionId);
            var text        = message.Trim();

            switch (channel.ToLowerInvariant())
            {
                case "global":
                    await Clients.All.SendAsync("ChatMessage", displayName, text, "global");
                    break;

                case "party":
                    var partyId = party.GetPartyId(displayName);
                    if (partyId is not null)
                        await Clients.Group(PartyService.PartyGroup(partyId))
                            .SendAsync("ChatMessage", displayName, text, "party");
                    else
                        await Clients.Caller.SendAsync("ChatMessage", "System",
                            "You are not in a party.", "room");
                    break;

                case "whisper":
                    if (string.IsNullOrWhiteSpace(target)) return;
                    var targetConn = presence.GetConnectionByName(target.Trim());
                    if (targetConn is null)
                    {
                        await Clients.Caller.SendAsync("ChatMessage", "System",
                            $"{target.Trim()} is not online.", "whisper");
                        return;
                    }
                    if (await IsBlockedByAsync(Context.User!.Identity!.Name!, target.Trim()))
                    {
                        await Clients.Caller.SendAsync("ChatMessage", "System",
                            $"You cannot message {target.Trim()}.", "whisper");
                        return;
                    }
                    await Clients.Client(targetConn).SendAsync("ChatMessage", displayName, text, "whisper");
                    await Clients.Caller.SendAsync("ChatMessage", $"-> {target.Trim()}", text, "whisper");
                    break;

                case "guild":
                {
                    using var guildScope = scopeFactory.CreateScope();
                    var guildDb  = guildScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var gCharId  = await GetCharacterIdByNameAsync(guildDb, displayName);
                    if (gCharId != 0)
                    {
                        var guildId = await GetGuildIdForCharacterAsync(guildDb, gCharId);
                        if (guildId is not null)
                            await Clients.Group(GuildService.GuildGroup(guildId.Value))
                                .SendAsync("ChatMessage", displayName, text, "guild");
                        else
                            await Clients.Caller.SendAsync("ChatMessage", "System",
                                "You are not in a guild.", "guild");
                    }
                    break;
                }

                default:
                    if (roomId is int rid)
                        await Clients.Group(RoomGroup(rid)).SendAsync("ChatMessage", displayName, text, "room");
                    break;
            }
        }

        // ── Game actions ──────────────────────────────────────────────────────

        /// <summary>
        /// Attempts a gather in the given room. The server picks the first spot the player
        /// has the right tool for. Consume one daily charge per success.
        /// </summary>
        public async Task<GatherActionResult> Gather(int roomId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new GatherActionResult(false, "no_session", null, 0, 0, null);
            using var held = await locks.AcquireAsync(player.Name);

            if (presence.GetRoom(Context.ConnectionId) != roomId)
                return new GatherActionResult(false, "wrong_room", null, 0, 0, null);

            if (session.GetEncounter(Context.ConnectionId) != null)
                return new GatherActionResult(false, "in_combat", null, 0, 0, null);

            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId);
            if (room == null || room.GatheringSpots.Count == 0)
                return new GatherActionResult(false, "no_spots", null, 0, 0, null);

            if (!session.TryConsumeGather(player.Name, roomId, room.DailyGatherLimit + JobManager.GetGatherKnowledgeBonus(player), out int remaining))
                return new GatherActionResult(false, "depleted", null, 0, 0, null, 0);

            foreach (var spot in room.GatheringSpots)
            {
                if (!string.IsNullOrEmpty(spot.RequiredToolId))
                {
                    bool hasTool = player.Inventory.Items.Any(i => i.Id == spot.RequiredToolId)
                                || player.WeaponSlot?.Id == spot.RequiredToolId;
                    if (!hasTool) continue;
                }

                if (!ItemFactory.TryCreateItem(spot.GatheredItemId, out var item)) continue;

                string jobId = spot.Type switch
                {
                    GatheringType.Ore  => "miner",
                    GatheringType.Tree => "woodcutter",
                    GatheringType.Herb => "herbalist",
                    _                  => ""
                };
                if (!string.IsNullOrEmpty(jobId))
                    item.StackSize = JobManager.GetGatherAmount(player, jobId);
                if (item.StackSize <= 0)
                    item.StackSize = 1;

                // Capture before AddItem — it decrements StackSize during stack-merging.
                int gatheredAmount = item.StackSize;
                player.Inventory.AddItem(item, player, "gather");

                long xp = 0;
                if (!string.IsNullOrEmpty(jobId))
                {
                    JobManager.GrantSkillXp(player, jobId, 10);
                    xp = 10;
                    await TryApplyRookieJobXpBonusAsync(player, jobId, xp);
                }

                await Clients.OthersInGroup(RoomGroup(roomId)).SendAsync("CharacterGathering", DisplayName());
                await NotifyCharacterUpdate(player, inventory: true, jobs: !string.IsNullOrEmpty(jobId));
                return new GatherActionResult(true, null, item.Id, gatheredAmount, xp, jobId, remaining);
            }

            return new GatherActionResult(false, "no_tool", null, 0, 0, null);
        }

        /// <summary>Crafts <paramref name="quantity"/> of <paramref name="recipeId"/> at <paramref name="npcId"/>.</summary>
        public async Task<CraftActionResult> Craft(string npcId, string recipeId, int quantity)
        {
            // Bounded batch: Amount * quantity used to overflow int (quantity 2^30), which passed the
            // ingredient check, consumed nothing and produced ~10^9 items (A06 F3).
            if (quantity <= 0 || quantity > MaxCraftBatch)
                return new CraftActionResult(false, "invalid_quantity", null, 0, 0, null);

            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new CraftActionResult(false, "no_session", null, 0, 0, null);

            using var held = await locks.AcquireAsync(player.Name);
            if (!CharacterRoomHasNpc(npcId))
                return new CraftActionResult(false, "npc_not_here", null, 0, 0, null);

            return await ExecuteCraftAsync(player, npcId, recipeId, quantity);
        }

        /// <summary>
        /// Crafts from the Jobs page, without standing at an NPC - only for the caller's active
        /// job, and only if that job opted in (Job.AllowJobPageCrafting). The craft NPC is resolved
        /// server-side from the job, never taken from the client, so the NPC-bound jobs (smith,
        /// tailor, ...) can't use this to skip their proximity check.
        /// </summary>
        public async Task<CraftActionResult> CraftFromJob(string recipeId, int quantity)
        {
            if (quantity <= 0 || quantity > MaxCraftBatch)
                return new CraftActionResult(false, "invalid_quantity", null, 0, 0, null);

            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new CraftActionResult(false, "no_session", null, 0, 0, null);

            using var held = await locks.AcquireAsync(player.Name);
            var npc = JobManager.GetJobPageCraftNpc(player.ActiveJobId);
            if (npc == null)
                return new CraftActionResult(false, "job_crafting_not_allowed", null, 0, 0, null);

            return await ExecuteCraftAsync(player, npc.Id, recipeId, quantity);
        }

        /// <summary>
        /// Shared craft execution behind <see cref="Craft"/> (NPC must be here) and
        /// <see cref="CraftFromJob"/> (opted-in active job). Validates recipe, knowledge and
        /// ingredients, then consumes and produces. The caller holds the character lock and has
        /// already validated quantity and its own location/job rule.
        /// </summary>
        private async Task<CraftActionResult> ExecuteCraftAsync(
            Myria.Lib.Core.Entities.Characters.Character player, string npcId, string recipeId, int quantity)
        {
            var recipe = CraftingService.GetRecipe(npcId, recipeId);
            if (recipe == null)
                return new CraftActionResult(false, "unknown_recipe", null, 0, 0, null);

            NpcService.TryGet(npcId, out var npc);
            if (npc?.MasterJobId is { Length: > 0 } masterJob)
            {
                int knowledge = JobXpService.GetLevel(JobManager.GetOrAdd(player, masterJob).KnowledgeXp);
                if (knowledge < recipe.RequiredKnowledgeLevel)
                    return new CraftActionResult(false, "knowledge_required", null, 0, 0, null);
            }

            // Validate ingredients (in long - no overflow even with odd recipe data)
            foreach (var ing in recipe.Ingredients)
            {
                long owned = player.Inventory.Items.Where(i => i.Id == ing.ItemId).Sum(i => (long)i.StackSize);
                if (ing.Amount <= 0 || owned < (long)ing.Amount * quantity)
                    return new CraftActionResult(false, "missing_ingredients", null, 0, 0, null);
            }

            // The product must exist and fit before anything is consumed (it used to be lost silently).
            if (!ItemFactory.TryCreateItem(recipeId, out var output) || output is null)
                return new CraftActionResult(false, "item_not_found", null, 0, 0, null);
            if (!player.Inventory.CanAdd(output, quantity))
                return new CraftActionResult(false, "inventory_full", null, 0, 0, null);

            // Consume ingredients
            foreach (var ing in recipe.Ingredients)
            {
                int remaining = ing.Amount * quantity;   // bounded: owned >= this, checked above
                foreach (var stack in player.Inventory.Items.Where(i => i.Id == ing.ItemId).ToList())
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(stack.StackSize, remaining);
                    stack.StackSize -= take;
                    remaining -= take;
                    if (stack.StackSize <= 0) player.Inventory.RemoveItem(stack);
                }
            }

            // Apply craft quality from smith skill level
            if (output is EquipmentItem crafted && npc?.MasterJobId is { Length: > 0 } mj)
            {
                crafted.CraftQuality = (float)JobXpService.GetSkillMultiplierFromXp(JobManager.GetOrAdd(player, mj).SkillXp);
                crafted.Bonuses = crafted.BaseStats.Scale(crafted.CraftQuality);
            }

            output.StackSize = quantity;
            player.Inventory.AddItem(output, player);

            long xp = 0;
            string? jobId = npc?.MasterJobId;
            if (!string.IsNullOrEmpty(jobId))
            {
                xp = recipe.XpReward * quantity;
                JobManager.GrantSkillXp(player, jobId, xp);
                await TryApplyRookieJobXpBonusAsync(player, jobId, xp);
            }

            if (presence.GetRoom(Context.ConnectionId) is int craftRoomId)
                await Clients.OthersInGroup(RoomGroup(craftRoomId)).SendAsync("CharacterCrafting", DisplayName());

            await NotifyCharacterUpdate(player, inventory: true, jobs: !string.IsNullOrEmpty(jobId));
            return new CraftActionResult(true, null, recipeId, quantity, xp, jobId);
        }

        /// <summary>Upgrades the given item at the given NPC, consuming upgrade materials.</summary>
        public async Task<UpgradeActionResult> Upgrade(string npcId, string itemId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new UpgradeActionResult(false, "no_session", null, 0, 0, null);
            using var held = await locks.AcquireAsync(player.Name);

            if (!CharacterRoomHasNpc(npcId))
                return new UpgradeActionResult(false, "npc_not_here", null, 0, 0, null);

            NpcService.TryGet(npcId, out var npc);

            var eq = player.Inventory.Items.OfType<EquipmentItem>()
                         .FirstOrDefault(e => e.Id == itemId);
            if (eq == null)
                return new UpgradeActionResult(false, "item_not_found", itemId, 0, 0, null);

            string matId    = CraftingService.UpgradeMaterialFor(npc?.UpgradeCategory);
            int    matCount = CraftingService.UpgradeMaterialCount(eq.UpgradeLevel);

            int knowledgeMax = 10;
            if (npc?.MasterJobId is { Length: > 0 } upgradeJob)
            {
                int knowledgeLevel = JobXpService.GetLevel(JobManager.GetOrAdd(player, upgradeJob).KnowledgeXp);
                knowledgeMax = JobXpService.GetMaxUpgradeLevel(knowledgeLevel);
            }
            if (eq.UpgradeLevel >= knowledgeMax)
                return new UpgradeActionResult(false, "knowledge_required", itemId, eq.UpgradeLevel, 0, null);

            int have = player.Inventory.Items.Where(i => i.Id == matId).Sum(i => i.StackSize);
            if (have < matCount)
                return new UpgradeActionResult(false, "missing_materials", itemId, eq.UpgradeLevel, 0, null);

            // Consume materials
            int remaining = matCount;
            foreach (var stack in player.Inventory.Items.Where(i => i.Id == matId).ToList())
            {
                if (remaining <= 0) break;
                int take = Math.Min(stack.StackSize, remaining);
                stack.StackSize -= take;
                remaining -= take;
                if (stack.StackSize <= 0) player.Inventory.RemoveItem(stack);
            }

            // Apply skill quality bonus from smith level
            if (npc?.MasterJobId is { Length: > 0 } mj)
            {
                float quality = (float)JobXpService.GetSkillMultiplierFromXp(JobManager.GetOrAdd(player, mj).SkillXp);
                if (quality > eq.CraftQuality) eq.CraftQuality = quality;
            }

            if (!eq.TryUpgrade(player, knowledgeMax))
                return new UpgradeActionResult(false, "upgrade_failed", itemId, eq.UpgradeLevel, 0, null);

            long xp = 0;
            string? jobId = npc?.MasterJobId;
            if (!string.IsNullOrEmpty(jobId))
            {
                xp = 25;
                JobManager.GrantSkillXp(player, jobId, xp);
                await TryApplyRookieJobXpBonusAsync(player, jobId, xp);
            }

            if (presence.GetRoom(Context.ConnectionId) is int upgradeRoomId)
                await Clients.OthersInGroup(RoomGroup(upgradeRoomId)).SendAsync("CharacterUpgrading", DisplayName());

            // Only the material consumption (loose Items list) is covered by the generic
            // snapshot - the upgraded equipment's own UpgradeLevel/CraftQuality/Bonuses live on
            // the item instance itself, which InventoryItemSnapshot (id + stack size) can't
            // represent, so the client's own eq.TryUpgrade() mirror stays the source of truth
            // for that part.
            await NotifyCharacterUpdate(player, inventory: true, jobs: !string.IsNullOrEmpty(jobId));
            return new UpgradeActionResult(true, null, itemId, eq.UpgradeLevel, xp, jobId);
        }

        // ── NPC Shop ──────────────────────────────────────────────────────────

        public Task<List<string>> GetNpcShopItems(string npcId)
        {
            if (!NpcService.TryGet(npcId, out var npc) || npc == null)
                return Task.FromResult(new List<string>());

            // A general trader (shop_general but not shop_equipment) also sells whatever
            // equipment no specialist in the same city covers - GetEffectiveShopItemNames
            // computes that fallback dynamically; raw npc.ItemNames alone omits it.
            var player = session.Get(Context.ConnectionId);
            return Task.FromResult(NpcService.GetEffectiveShopItemNames(npc, player?.CurrentRoom));
        }

        public async Task<NpcShopBuyResult> BuyFromNpcShop(string npcId, string itemId, int quantity)
        {
            if (quantity <= 0) return new NpcShopBuyResult(false, "invalid_quantity", 0, 0);

            var player = session.Get(Context.ConnectionId);
            if (player == null) return new NpcShopBuyResult(false, "no_session", 0, 0);

            // Validate NPC exists and sells this item (server's unmodded data only).
            if (!NpcService.TryGet(npcId, out var npc) || npc == null)
                return new NpcShopBuyResult(false, "invalid_npc", 0, 0);

            if (!NpcService.GetEffectiveShopItemNames(npc, player.CurrentRoom).Contains(itemId, StringComparer.OrdinalIgnoreCase))
                return new NpcShopBuyResult(false, "item_not_sold", 0, 0);

            // Create item from server's unmodded item registry.
            if (!ItemFactory.TryCreateItem(itemId, out var item) || item == null)
                return new NpcShopBuyResult(false, "invalid_item", 0, 0);

            // Class restriction check for equipment.
            if (item is EquipmentItem eq && !eq.IsTool && !eq.IsUsableBy(player))
                return new NpcShopBuyResult(false, "wrong_class", 0, 0);

            using var held = await locks.AcquireAsync(player.Name);

            long totalCost = (long)item.BuyPrice * quantity;
            if (!player.Money.CanAfford(totalCost))
                return new NpcShopBuyResult(false, "not_enough_money", totalCost, quantity);

            // Space for the whole amount first, then pay, then add. A full inventory used to top up an
            // existing stack and return "full" before paying - free items (A06 F4).
            if (!player.Inventory.CanAdd(item, quantity))
                return new NpcShopBuyResult(false, "inventory_full", totalCost, quantity);
            if (!player.Money.TrySpend(totalCost))
                return new NpcShopBuyResult(false, "not_enough_money", totalCost, quantity);

            item.StackSize = quantity;
            player.Inventory.AddItem(item, player);

            // v0.3 world state: the NPC's stock of the item - and of what it's made from here - goes down.
            if (presence.GetRoom(Context.ConnectionId) is int buyRoomId && CharacterRoomHasNpc(npcId))
            {
                var ingredients = CraftingService.GetRecipe(npcId, itemId)?.Ingredients
                    .Select(i => (i.ItemId, i.Amount)) ?? Enumerable.Empty<(string, int)>();
                world.State?.RecordPurchase(buyRoomId, npcId, itemId, quantity, ingredients);
            }

            // Credit guild treasury if buyer is a rookie
            await TryApplyPurchaseCommissionAsync(player, totalCost);

            await NotifyCharacterUpdate(player, inventory: true, money: true);
            return new NpcShopBuyResult(true, null, totalCost, quantity);
        }

        /// <summary>
        /// Mirrors client-side NPC selling (InventoryGridViewModel.SellAmount) onto the live
        /// session character - without this, selling only ever mutated the client's local
        /// Character (inventory + gold), which the server's session copy never learns about, so
        /// the sale is silently lost on the next save (e.g. disconnect).
        /// </summary>
        public async Task<NpcSellResult> SellItemToNpc(string itemId, int quantity)
        {
            if (quantity <= 0) return new NpcSellResult(false, "invalid_quantity", 0, 0);

            var player = session.Get(Context.ConnectionId);
            if (player == null) return new NpcSellResult(false, "no_session", 0, 0);

            using var held = await locks.AcquireAsync(player.Name);
            var invItem = player.Inventory.Items.FirstOrDefault(i => i.Id == itemId);
            if (invItem == null)
                return new NpcSellResult(false, "not_owned", 0, 0);

            if (invItem.StackSize < quantity)
                return new NpcSellResult(false, "not_enough_amount", 0, 0);

            long totalGain = (long)Myria.Lib.Core.Services.Manager.JobManager.GetSellValue(invItem, player) * quantity;
            if (!player.Money.CanHold(totalGain))
                return new NpcSellResult(false, "money_full", 0, 0);   // items used to vanish unpaid

            if (invItem.StackSize == quantity)
                player.Inventory.RemoveItem(invItem);
            else
                invItem.StackSize -= quantity;

            player.Money.TryAdd(totalGain);

            // v0.3 world state: a sale refills the stock of whichever NPC here keeps this item and is
            // lowest on it - the same deposit a quest turn-in makes.
            if (presence.GetRoom(Context.ConnectionId) is int sellRoomId
                && RoomService.GetRoomById(sellRoomId) is { } sellRoom
                && world.State?.NeediestNpc(sellRoomId, sellRoom.Npcs, itemId) is { } stockKey)
                world.State.Deposit(stockKey, quantity);

            logger.LogInformation("{Character} sold {Qty}x {Item} to an NPC for {Gain} gold",
                DisplayName(), quantity, itemId, totalGain);

            await NotifyCharacterUpdate(player, inventory: true, money: true);
            return new NpcSellResult(true, null, totalGain, quantity);
        }

        // ── Inventory ─────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors client-side stat point allocation (CharacterPageViewModel.IncreaseStat/
        /// DecreaseStat) onto the live session character. Those +/- buttons only mutate the
        /// client's local Character - without this, the server's session copy (what Heal,
        /// combat, etc. actually compute MaxHealth/MaxMana from) keeps whatever stats existed at
        /// LoadCharacter time, so e.g. the Healer would only ever heal to "max HP at login".
        /// Validates the submitted totals against this character's earned level-up points below -
        /// matches EquipItem/UnequipItem in trusting which items/stats are assigned, but (unlike
        /// those) still bounds the numbers since unlimited stat points would be a direct power
        /// cheat rather than just a cosmetic mismatch.
        /// </summary>
        public Task SyncStatAllocation(int strengthAdded, int dexterityAdded, int enduranceAdded,
            int intelligenceAdded, int spiritAdded, int unusedPoints)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.CompletedTask;

            // The client computes this locally for responsive UI, but it's still client-submitted
            // input over the wire - reject anything a modified client couldn't have legitimately
            // reached by spending this character's own level-up points (1 per level from level 2
            // onward, the same formula Character.RecalculateUnusedPoints uses), so a player can't
            // grant themselves free stat points.
            if (strengthAdded < 0 || dexterityAdded < 0 || enduranceAdded < 0 ||
                intelligenceAdded < 0 || spiritAdded < 0 || unusedPoints < 0)
                return Task.CompletedTask;

            int totalPointsEarned = Math.Max(0, player.Level - 1);
            // Widened to long before summing - the five components are individually >= 0 but
            // otherwise attacker-controlled, and a plain int sum can wrap negative and slip past
            // the cap check below (e.g. two components near int.MaxValue).
            long pointsSpent = (long)strengthAdded + dexterityAdded + enduranceAdded + intelligenceAdded + spiritAdded;
            if (pointsSpent + unusedPoints > totalPointsEarned)
                return Task.CompletedTask;

            player.Stats.StrengthAdded     = strengthAdded;
            player.Stats.DexterityAdded    = dexterityAdded;
            player.Stats.EnduranceAdded    = enduranceAdded;
            player.Stats.IntelligenceAdded = intelligenceAdded;
            player.Stats.SpiritAdded       = spiritAdded;
            player.Stats.UnusedPoints      = unusedPoints;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Mirrors client-side quest accept/return (QuestDialogPanelViewModel.ExecuteQuestAction)
        /// onto the live session character - without this, quest turn-in rewards (including XP,
        /// which is why "the client levels up but the server doesn't" for quests) only ever
        /// applied to the client's local Character and never reached the server's session copy.
        /// </summary>
        public async Task<bool> QuestAction(string questId, bool isReturn)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

            var quest = QuestManager.GetQuestById(questId);
            if (quest == null) return false;

            using var held = await locks.AcquireAsync(player.Name);

            if (isReturn)
            {
                var active = player.ActiveQuests.FirstOrDefault(q => q.Id == quest.Id);
                if (active == null) return false;

                // Handed in at the NPC that takes it back (like the client's quest dialog).
                if (!string.IsNullOrEmpty(active.ReturnNpcIdResolved) && !CharacterRoomHasNpc(active.ReturnNpcIdResolved))
                    return false;

                // Re-verify kill/item objectives against this session's own server-tracked
                // progress before granting rewards - previously trusted the client's claim the
                // quest was done, so a client that turned in early (bug, desync, or a modified
                // client) got full rewards for an incomplete quest while the server's own
                // KillProgress/ItemProgress counters said otherwise. Same check as
                // Character.ValidateQuestStatuses, which already corrects this after the fact for
                // saved data; this stops it from happening live.
                // Item counters are recounted from what the character actually carries right now - they
                // only ever went up, so items sold or used up after the counter was full still counted.
                QuestManager.UpdateItemProgress(player);
                if (!QuestManager.ObjectivesMet(active)) return false;

                active.GrantRewards(player);

                if (active.IsRepeatable)
                {
                    if (!player.RepeatableQuestRecords.TryGetValue(active.Id, out var rec))
                    {
                        rec = new RepeatRecord();
                        player.RepeatableQuestRecords[active.Id] = rec;
                    }
                    if (rec.LastCompletionDate?.Date != DateTime.Today)
                        rec.CompletionsToday = 0;
                    rec.TimesCompleted++;
                    rec.CompletionsToday++;
                    rec.LastCompletionDate = DateTime.Now;
                    player.ActiveQuests.Remove(active);
                }
                else
                {
                    active.Status = QuestStatus.Returned;
                    player.CompletedQuests.Add(active);
                    player.ActiveQuests.Remove(active);
                }
            }
            else
            {
                // All acceptance conditions, server-side: only "not already active" used to be checked,
                // so completed quests (talk-only ones complete instantly) could be accepted and handed
                // in again in a loop, paying out every time (A06 F5). Plus: the giver must be here.
                var partyId = party.GetPartyId(DisplayName());
                int partySize = partyId is null ? 1 : party.GetMembers(partyId).Count;
                if (!QuestManager.IsAcceptable(quest, player, partySize))
                    return false;
                if (!string.IsNullOrEmpty(quest.GiverNpcId) && !CharacterRoomHasNpc(quest.GiverNpcId))
                    return false;

                // Also counts items the character already carries (they used to show 0/N until a new one
                // was picked up).
                QuestManager.StartQuest(player, quest);
            }

            // Covers both directions: a turn-in's item/gold/XP rewards, and an accept's
            // GrantAcceptItems - the client's own base.ExecuteQuestAction mirror already applies
            // these locally for instant feedback, this is the reconciling confirmation (and the
            // only thing that ever tells the client about quest-driven XP/level changes, which
            // it doesn't attempt to replay itself).
            await NotifyCharacterUpdate(player, inventory: true, money: true, progress: true, questProgress: true, jobs: true);
            return true;
        }

        /// <summary>Mirrors client-side rune granting (RuneDrawingViewModel.ExecuteGrantRune) onto
        /// the live session character - see GameHub class remarks on why this matters.</summary>
        public async Task<bool> GrantRune(string baseRuneId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

            BaseRuneService.GrantBaseRune(player, baseRuneId);
            // GrantBaseRune dedupes against KnownRunes itself, so this also confirms the client's
            // own optimistic add didn't diverge (e.g. a duplicate grant that the server silently
            // no-op'd but the client would otherwise think succeeded).
            await NotifyCharacterUpdate(player, runes: true);
            return true;
        }

        /// <summary>Mirrors client-side active-job switching (JobsPageViewModel.SetActive /
        /// JobMasterPanelViewModel.ExecuteJobToggle) onto the live session character.</summary>
        public Task<bool> ToggleJob(string? jobId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);

            return Task.FromResult(JobManager.SetActiveJob(player, jobId));
        }

        /// <summary>Mirrors client-side class switching (ClassPanelViewModel.ExecuteClassChange)
        /// onto the live session character.</summary>
        public Task<bool> ChangeClass(string cls)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);

            return Task.FromResult(ClassManager.SetClass(player, cls));
        }

        public async Task<EquipItemResult> EquipItem(string itemId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return new EquipItemResult(false, "Not connected to a game session.");
            using var held = await locks.AcquireAsync(player.Name);

            bool ok = player.Inventory.SwapEquipment(itemId, player, out var reason);
            // Echoes back which item id ended up in which slot (equipment: true), not just the
            // loose Items list - without this, the client's own base.ExecuteEquip mirror was the
            // only source of truth for what's equipped, and a mismatch (e.g. a rejected swap the
            // client didn't correctly roll back) would permanently desync MaxHealth/MaxMana/combat
            // stats (all gear-derived) between client display and server-authoritative combat.
            if (ok) await NotifyCharacterUpdate(player, inventory: true, equipment: true);
            return new EquipItemResult(ok, reason);
        }

        public async Task<bool> UnequipItem(string slotType)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;
            using var held = await locks.AcquireAsync(player.Name);

            EquipmentItem? item = slotType switch
            {
                "Weapon"    => player.WeaponSlot,
                "Armor"     => player.ArmorSlot,
                "Accessory" => player.AccessorySlot,
                _           => null
            };
            if (item == null) return false;

            if (!player.Inventory.AddItem(item, player, "unequip"))
                return false;

            switch (slotType)
            {
                case "Weapon":    player.WeaponSlot    = null; break;
                case "Armor":     player.ArmorSlot     = null; break;
                case "Accessory": player.AccessorySlot = null; break;
            }
            await NotifyCharacterUpdate(player, inventory: true, equipment: true);
            return true;
        }

        public async Task<bool> UseItem(string itemId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;
            using var held = await locks.AcquireAsync(player.Name);
            bool used = player.Inventory.UseItem(itemId, player);
            // Covers both the consumed item leaving the inventory and any HP/MP a healing potion
            // etc. restored (Inventory.UseItem already ran the consumable's effect on this same
            // session player by the time we get here).
            if (used) await NotifyCharacterUpdate(player, inventory: true, vitals: true);
            return used;
        }

        // ── Skills ────────────────────────────────────────────────────────────

        public Task<bool> SlotSkill(string source, string skillId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);
            if (!Enum.TryParse<SlottedSkillSource>(source, out var src)) return Task.FromResult(false);
            return Task.FromResult(SkillSlotService.TryAddSlot(player, src, skillId));
        }

        public Task<bool> UnslotSkill(string source, string skillId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);
            if (!Enum.TryParse<SlottedSkillSource>(source, out var src)) return Task.FromResult(false);
            return Task.FromResult(SkillSlotService.RemoveSlot(player, src, skillId));
        }

        public Task<bool> ReorderSkillSlot(int fromIndex, int toIndex)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);
            SkillSlotService.ReorderSlots(player, fromIndex, toIndex);
            return Task.FromResult(true);
        }

        /// <summary>Spends one of a skill's own unspent points on one of its upgrade options - see
        /// SkillLevelingService. Server-authoritative: re-derives UnspentPoints from UsageCount
        /// before honoring the spend, same anti-tamper approach as stat-point allocation.</summary>
        public async Task<bool> SpendSkillPoint(string skillId, string upgradeId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

            var skill = player.Skills.FirstOrDefault(s => s.Id == skillId);
            if (skill == null) return false;

            bool ok = SkillLevelingService.TrySpendPoint(player, skill, upgradeId, out _);
            if (ok) await NotifyCharacterUpdate(player, skillProgress: true);
            return ok;
        }

        /// <summary>Refunds a skill's purchased upgrades for a flat cost
        /// (SkillLevelingService.RespecCostBronze, 100 silver) so the points can be spent differently.</summary>
        public async Task<bool> RespecSkill(string skillId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

            if (!player.Money.TrySpend(SkillLevelingService.RespecCostBronze)) return false;

            SkillLevelingService.Respec(player, skillId);
            await NotifyCharacterUpdate(player, money: true, skillProgress: true);
            return true;
        }

        // ── Combat ────────────────────────────────────────────────────────────

        /// <summary>Abandons any active combat (solo or group) without awarding loot or XP.</summary>
        public async Task AbandonCombat()
        {
            session.RemoveEncounter(Context.ConnectionId);

            // Fixes TODO.md item 51: this used to only unregister the fleeing connection from
            // groupCombat, never touching the shared GroupCombatEncounter's own roster/turn
            // order - if it was (or became) the fled character's turn, nobody could ever act for
            // them again, silently stranding every other still-fighting party member with their
            // own actions rejected forever (IsThisCharactersTurn never matched them). Resolve the
            // fight/encounter BEFORE unregistering the connection (need the fightId that
            // RemoveConn would otherwise erase), call the new GroupCombatEncounter.RemoveCharacter
            // to fix up the turn order for whoever's left, then unregister the connection as
            // before. Broadcasting after RemoveConn is deliberate here (unlike item 52's
            // finish-broadcast ordering) since CombatGroupClients' recipient list should exclude
            // the fleeing connection itself - they already got their own local "fled" navigation
            // client-side and don't need this update.
            var fightId = groupCombat.GetPartyId(Context.ConnectionId);
            if (fightId is not null && groupCombat.GetEncounter(fightId) is { } encounter)
            {
                int logBefore = encounter.Log.Count;
                encounter.RemoveCharacter(DisplayName());
                groupCombat.RemoveConn(Context.ConnectionId);

                if (encounter.IsFinished)
                {
                    // Nobody was left standing (the last remaining member just fled too) - no
                    // one left to notify, just clean up the now-dead encounter's registration.
                    groupCombat.RemoveByParty(fightId);
                }
                else
                {
                    var snap = BuildGroupSnapshot(encounter, logBefore);
                    await CombatGroupClients(fightId).SendAsync("GroupCombatUpdated", snap);
                }
            }
            else
            {
                groupCombat.RemoveConn(Context.ConnectionId);
            }
        }

        /// <summary>Starts a combat encounter for the player in the given room.</summary>
        public async Task<StartCombatResult> StartCombat(int roomId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new StartCombatResult(false, "no_session", null, 0, 0);

            if (!player.IsAlive)
                return new StartCombatResult(false, "player_dead", null, 0, 0);

            if (presence.GetRoom(Context.ConnectionId) != roomId)
                return new StartCombatResult(false, "wrong_room", null, 0, 0);

            if (session.GetEncounter(Context.ConnectionId) != null || pvp.IsInFight(Context.ConnectionId))
                return new StartCombatResult(false, "already_in_combat", null, 0, 0);

            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId);
            if (room == null)
                return new StartCombatResult(false, "no_monsters", null, 0, 0);

            // For dungeon rooms use spawned CurrentMonsters; otherwise use templates.
            var monsterPool = room.IsDungeonRoom && room.CurrentMonsters.Count > 0
                ? room.CurrentMonsters
                : room.Monsters;

            if (monsterPool.Count == 0)
                return new StartCombatResult(false, "no_monsters", null, 0, 0);

            // v0.3: an overworld room's population of a monster type can run out (dungeons keep
            // their own spawn-and-clear cycle).
            var picked = room.IsDungeonRoom
                ? MonsterService.PickMonsterForFight(monsterPool, room.EncounterableMonsters)
                : MonsterService.TryPickMonsterForFight(monsterPool, room.EncounterableMonsters, m => IsMonsterAvailable(roomId, m));
            if (picked == null)
                return new StartCombatResult(false, "no_monsters", null, 0, 0);
            var monster = picked.Clone();
            var encounter = new CombatEncounter(player, monster);
            encounter.MonsterKilled += (_, e) => world.State?.RecordKill(roomId, e.MonsterId);
            session.SetEncounter(Context.ConnectionId, encounter);

            await Clients.OthersInGroup(RoomGroup(roomId)).SendAsync("CharacterInCombat", DisplayName());

            return new StartCombatResult(
                true, null,
                monster.Name,
                monster.CurrentHealth,
                monster.MaxHealth,
                monster.Level);
        }

        /// <summary>Performs one basic attack in the current combat encounter.</summary>
        public async Task<CombatTurnResult> CharacterAttack()
        {
            var result = await RunCombatAction(enc => enc.CharacterAttack());
            var player = session.Get(Context.ConnectionId);
            return player is null ? result : await FinishSoloActionAsync(player, result);
        }

        /// <summary>Uses one consumable (by item id) from the caller's own inventory as their turn
        /// in the current solo encounter. Resolved server-side from the session inventory.</summary>
        public async Task<CombatTurnResult> CharacterUseItem(string itemId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null || session.GetEncounter(Context.ConnectionId) == null)
                return EmptyCombatTurn();

            CombatTurnResult result;
            // Same per-character lock as the out-of-combat UseItem (see GroupCharacterUseItem).
            using (await locks.AcquireAsync(player.Name))
            {
                var item = ResolveConsumable(player, itemId);
                if (item == null) return EmptyCombatTurn();

                // The engine's bool is deliberately ignored (like CharacterCastSkill): it's also
                // false when a stun or a DoT death already resolved the turn, and that result
                // (incl. a loss -> respawn below) must still reach the client.
                result = await RunCombatAction(enc => enc.CharacterUseItem(item));
            }

            await NotifyCharacterUpdate(player, inventory: true);
            return await FinishSoloActionAsync(player, result);
        }

        /// <summary>
        /// Post-turn handling shared by every solo combat action: respawn on a loss, and on a win
        /// fold any rookie XP bonus into the result.
        /// </summary>
        private async Task<CombatTurnResult> FinishSoloActionAsync(Myria.Lib.Core.Entities.Characters.Character player, CombatTurnResult result)
        {
            if (result.Finished && !result.CharacterWon)
            {
                await RespawnCharacterAsync(player, Context.ConnectionId);
            }
            else if (result.Finished && result.CharacterWon && result.XpGained > 0)
            {
                long bonus = await TryApplyRookieXpBonusAsync(player, result.XpGained);
                // The bonus is applied to the server's character above, but was granted after
                // `result` was already built - without folding it in here, the client never
                // learns about the extra XP/levels/stat growth at all (only a full reload would
                // pick it up), silently drifting MaxHealth/MaxMana out of sync for rookies.
                if (bonus > 0)
                    result = result with { RookieBonusXp = bonus };
            }
            return result;
        }

        /// <summary>Begins casting the named skill in the current combat encounter.</summary>
        public async Task<CombatTurnResult> CharacterCastSkill(string skillId)
        {
            var player   = session.Get(Context.ConnectionId);
            var encounter = session.GetEncounter(Context.ConnectionId);
            if (player == null || encounter == null)
                return EmptyCombatTurn();

            var skill = ResolveCastableSkill(player, skillId);

            if (skill == null) return EmptyCombatTurn();

            var result = await RunCombatAction(enc => enc.CharacterBeginCast(skill));
            return await FinishSoloActionAsync(player, result);
        }

        // ── Group Combat ──────────────────────────────────────────────────────

        /// <summary>
        /// Authoritative on-demand resync for a group fight the caller believes they're in -
        /// returns the live snapshot with no new log entries (the caller already has everything
        /// up to whatever they last saw). Two callers: (1) the client's own recovery path when a
        /// GroupCharacterAttack/CastSkill call comes back empty (turn info was stale - fetch the
        /// real state instead of guessing), and (2) defensively on entering the fight page, in
        /// case a broadcast was ever missed. Returns null if the caller isn't in an active group
        /// encounter (already ended, or never joined) - never a fabricated "empty" snapshot that
        /// could look like a real party of 0 characters.
        /// </summary>
        public Task<GroupCombatSnapshot?> GetGroupCombatState()
        {
            var partyId = groupCombat.GetPartyId(Context.ConnectionId);
            if (partyId is null) return Task.FromResult<GroupCombatSnapshot?>(null);

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null) return Task.FromResult<GroupCombatSnapshot?>(null);

            return Task.FromResult<GroupCombatSnapshot?>(BuildGroupSnapshot(encounter, encounter.Log.Count));
        }

        /// <summary>
        /// Starts a group-combat encounter. <paramref name="soloOnly"/> forces a 1-character,
        /// 1-monster fight even if the caller is in a party in this room - this is what the
        /// client's plain "Fight" button uses (as opposed to "Group Fight", which pulls in the
        /// caller's party automatically). Both now run on the same GroupCombatEncounter engine.
        /// </summary>
        public async Task<StartGroupCombatResult> StartGroupCombat(int roomId, bool soloOnly = false)
        {
            var displayName = DisplayName();

            var caller = session.Get(Context.ConnectionId);
            if (caller is null || !caller.IsAlive)
            {
                logger.LogWarning("Combat start rejected for {User} (conn {Conn}): player_dead", displayName, Context.ConnectionId);
                return new StartGroupCombatResult(false, "player_dead", [], [], "");
            }

            if (presence.GetRoom(Context.ConnectionId) != roomId)
            {
                logger.LogWarning("Combat start rejected for {User} (conn {Conn}): wrong_room", displayName, Context.ConnectionId);
                return new StartGroupCombatResult(false, "wrong_room", [], [], "");
            }

            // Reject if already in any group combat (or a PvP fight).
            if (groupCombat.GetPartyId(Context.ConnectionId) is not null || pvp.IsInFight(Context.ConnectionId))
            {
                logger.LogWarning("Combat start rejected for {User} (conn {Conn}): already_in_combat", displayName, Context.ConnectionId);
                return new StartGroupCombatResult(false, "already_in_combat", [], [], "");
            }

            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId);
            bool hasMonsters = room != null &&
                (room.IsDungeonRoom ? room.CurrentMonsters.Count > 0 : room.Monsters.Count > 0);
            if (!hasMonsters)
            {
                logger.LogWarning("Combat start rejected for {User} (conn {Conn}): no_monsters", displayName, Context.ConnectionId);
                return new StartGroupCombatResult(false, "no_monsters", [], [], "");
            }

            var partyId = soloOnly ? null : party.GetPartyId(displayName);

            List<Myria.Lib.Core.Entities.Characters.Character> players;
            List<string> memberConns;
            string       fightId;

            if (partyId is not null)
            {
                // Fights are keyed per (party, room) rather than just per party, so a party that's
                // spread across two rooms can have two independent fights running at once instead
                // of the second room's "Start Group Fight" either rejecting outright or - worse -
                // pulling in members who are still standing in the first room (see GetConnections'
                // and memberConns' room filters below, and CombatGroupClients).
                fightId = $"{partyId}:{roomId}";

                // The party already has a live fight in this exact room - join it instead of
                // rejecting. Reachable when a member arrives in the room (or simply presses the
                // button) after a roommate already started the fight; the earlier already_in_combat
                // check above only rejects the caller's *own* connection being mid-fight, not a
                // fight existing at all, so this is the first point that actually knows one does.
                if (groupCombat.GetEncounter(fightId) is { } existingEncounter)
                {
                    existingEncounter.AddCharacter(caller);
                    groupCombat.AddConnection(fightId, Context.ConnectionId);

                    var joinResult = new StartGroupCombatResult(
                        true, null,
                        BuildCharacterStates(existingEncounter),
                        BuildMonsterStates(existingEncounter),
                        existingEncounter.CurrentTurnCharacterName);

                    logger.LogInformation("Combat joined (party, fight {FightId}): {User}", fightId, displayName);

                    // The joiner needs the fight's full current state (their client hasn't seen any
                    // of it yet), same shape as a fresh start. Everyone already in the fight just
                    // needs to know a new combatant arrived - GroupCombatUpdated with the "joined"
                    // log entry AddCharacter just appended covers that without resending everything.
                    await Clients.Caller.SendAsync("GroupCombatStarted", joinResult);
                    var joinSnap = BuildGroupSnapshot(existingEncounter, existingEncounter.Log.Count - 1);
                    await Clients.Clients(groupCombat.GetConnections(fightId)
                            .Where(c => c != Context.ConnectionId).ToList())
                        .SendAsync("GroupCombatUpdated", joinSnap);

                    return joinResult;
                }

                // No fight in this room yet - gather all party members physically present here.
                var members = party.GetMembers(partyId);
                players = members
                    .Select(m => presence.GetConnectionByName(m))
                    .Where(c => c != null)
                    .Where(c => presence.GetRoom(c!) == roomId)
                    .Select(c => session.Get(c!)!)
                    .Where(p => p != null && p.IsAlive)
                    .ToList();

                memberConns = members
                    .Select(m => presence.GetConnectionByName(m))
                    .Where(c => c != null)
                    .Where(c => presence.GetRoom(c!) == roomId)
                    .Select(c => c!)
                    .ToList();
            }
            else
            {
                // Solo group fight — one player against multiple monsters.
                players     = new() { caller };
                memberConns = new() { Context.ConnectionId };
                fightId     = Context.ConnectionId;
            }

            if (players.Count == 0)
                return new StartGroupCombatResult(false, "no_session", [], [], "");

            // v0.3: only monster types the room still has a population of (dungeons unaffected).
            var monsters = MonsterService.TryPickMonstersForGroupFight(
                room!.Monsters, room.EncounterableMonsters, room.CurrentMonsters, room.IsDungeonRoom,
                m => IsMonsterAvailable(roomId, m), max: soloOnly ? 1 : 5);
            if (monsters.Count == 0)
                return new StartGroupCombatResult(false, "no_monsters", [], [], "");
            var encounter = new GroupCombatEncounter(players, monsters);
            encounter.MonsterKilled += (_, e) => world.State?.RecordKill(roomId, e.MonsterId);

            if (!groupCombat.Start(fightId, memberConns, encounter))
                return new StartGroupCombatResult(false, "already_in_combat", [], [], "");

            var result = new StartGroupCombatResult(
                true, null,
                BuildCharacterStates(encounter),
                BuildMonsterStates(encounter),
                encounter.CurrentTurnCharacterName);

            logger.LogInformation("Combat started ({Kind}, fight {FightId}): {Players}",
                soloOnly ? "solo" : "party", fightId, string.Join(", ", players.Select(p => p.Name)));

            await CombatGroupClients(fightId).SendAsync("GroupCombatStarted", result);

            return result;
        }

        public async Task<GroupCombatSnapshot> GroupCharacterAttack(int targetMonsterIndex)
        {
            var partyId = groupCombat.GetPartyId(Context.ConnectionId);
            if (partyId is null)
                return EmptyGroupSnapshot();

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null)
                return EmptyGroupSnapshot();

            int logBefore = encounter.Log.Count;
            string turnBefore = encounter.CurrentTurnCharacterName;
            bool ok = encounter.CharacterAttack(DisplayName(), targetMonsterIndex);
            if (!GroupActionResolved(encounter, ok, turnBefore))
                return EmptyGroupSnapshot();

            return await CompleteGroupActionAsync(encounter, partyId, logBefore);
        }

        public async Task<GroupCombatSnapshot> GroupCharacterCastSkill(string skillId, int targetIndex)
        {
            var player  = session.Get(Context.ConnectionId);
            var partyId = groupCombat.GetPartyId(Context.ConnectionId);
            if (player is null || partyId is null)
                return EmptyGroupSnapshot();

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null) return EmptyGroupSnapshot();

            var skill = ResolveCastableSkill(player, skillId);
            if (skill is null) return EmptyGroupSnapshot();

            int logBefore = encounter.Log.Count;
            string turnBefore = encounter.CurrentTurnCharacterName;
            bool ok = encounter.CharacterCastSkill(DisplayName(), skill, targetIndex);
            if (!GroupActionResolved(encounter, ok, turnBefore))
                return EmptyGroupSnapshot();

            return await CompleteGroupActionAsync(encounter, partyId, logBefore);
        }

        /// <summary>Uses one consumable (by item id) from the caller's own inventory as their turn
        /// in the current group fight. The item is resolved server-side from the caller's session
        /// inventory - the client only names which one.</summary>
        public async Task<GroupCombatSnapshot> GroupCharacterUseItem(string itemId)
        {
            var player  = session.Get(Context.ConnectionId);
            var partyId = groupCombat.GetPartyId(Context.ConnectionId);
            if (player is null || partyId is null)
                return EmptyGroupSnapshot();

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null) return EmptyGroupSnapshot();

            int logBefore = encounter.Log.Count;
            // Same per-character lock as the out-of-combat UseItem: the inventory must not change
            // underneath this (trade, shop deposit, selling) between resolving and consuming.
            using (await locks.AcquireAsync(player.Name))
            {
                var item = ResolveConsumable(player, itemId);
                if (item is null) return EmptyGroupSnapshot();

                string turnBefore = encounter.CurrentTurnCharacterName;
                bool ok = encounter.CharacterUseItem(DisplayName(), item);
                if (!GroupActionResolved(encounter, ok, turnBefore)) return EmptyGroupSnapshot();
            }

            // The snapshot carries HP/MP for everyone; the user's own inventory isn't part of it.
            await NotifyCharacterUpdate(player, inventory: true);
            return await CompleteGroupActionAsync(encounter, partyId, logBefore);
        }

        /// <summary>
        /// Whether a group action resolved a turn that must be broadcast/finished. The engine's
        /// bool alone isn't enough: it's also false when the actor died from a DoT tick at the
        /// start of their own turn - after the turn has already advanced, and possibly after that
        /// death ended the fight. Treating that as a rejection skipped the broadcast and the
        /// finish/respawn. A real rejection (not your turn, bad target, no mana, cooldown,
        /// silenced) neither advances the turn nor ends the fight, so it still returns false here.
        /// </summary>
        private static bool GroupActionResolved(GroupCombatEncounter encounter, bool engineResult, string turnBefore) =>
            engineResult
            || encounter.IsFinished
            || !string.Equals(encounter.CurrentTurnCharacterName, turnBefore, StringComparison.Ordinal);

        /// <summary>
        /// Everything that follows a successful group-combat action: build the turn's snapshot,
        /// then either broadcast it, or - if the action ended the fight - grant rookie bonuses,
        /// save, push the reconciling character updates, broadcast the finish, and respawn the dead.
        /// </summary>
        private async Task<GroupCombatSnapshot> CompleteGroupActionAsync(GroupCombatEncounter encounter, string partyId, int logBefore)
        {
            var snap = BuildGroupSnapshot(encounter, logBefore);

            if (!encounter.IsFinished)
            {
                await CombatGroupClients(partyId).SendAsync("GroupCombatUpdated", snap);
                return snap;
            }

            if (encounter.CharactersWon)
            {
                logger.LogInformation("Group combat won: {Chars}",
                    string.Join(", ", encounter.Characters.Select(c => $"{c.Name} (Lvl {c.Level})")));

                long totalMonsterExp = encounter.Monsters.Sum(m => m.Exp);
                var bonusByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in encounter.Characters.Where(p => p.IsAlive))
                {
                    // A rookie-bonus failure here must never block the save/broadcast/respawn
                    // below - the fight is already fully resolved in-memory (monsters dead,
                    // XP/levels applied), so an unguarded throw would silently strand the
                    // party mid-fight exactly like the bug fixed in item 52 (see item 66).
                    long bonus = await TryApplyRookieXpBonusAsync(p, totalMonsterExp);
                    if (bonus > 0) bonusByName[p.Name] = bonus;
                }

                await SaveCombatParticipantsAsync(encounter.Characters.Where(p => p.IsAlive));

                // `snap` was already built above (before the bonus existed), so a rookie's
                // extra XP/levels/stat growth would otherwise never reach the client at all -
                // only a full reload would pick it up. Patch the affected entries in place.
                if (bonusByName.Count > 0)
                {
                    snap = snap with
                    {
                        Characters = snap.Characters.Select(c =>
                            bonusByName.TryGetValue(c.Name, out var b) ? c with { RookieBonusXp = b } : c
                        ).ToList()
                    };
                }

                // GroupCombatSnapshot already carries per-turn HP/loot ids - this is the
                // reconciling push per surviving party member for everything that isn't
                // per-turn (items actually granted, XP/level/stats incl. rookie bonus, quest
                // kill/item progress), same as solo combat's RunCombatAction.
                foreach (var p in encounter.Characters.Where(p => p.IsAlive))
                {
                    if (session.GetConnectionId(p.Name) is string connId)
                        await NotifyCharacterUpdateFor(connId, p, inventory: true, progress: true, questProgress: true, skillProgress: true);
                }
            }

            // Broadcast BEFORE removing the party's fight registration - CombatGroupClients
            // resolves recipients via groupCombat.GetConnections(fightId), which reads the
            // same connection registry RemoveByParty tears down; removing first left that
            // lookup empty, so the finishing broadcast silently reached nobody and both
            // clients stayed frozen on the stale mid-fight screen forever (the server had
            // already saved/respawned everything correctly - only the client notification
            // was lost).
            await CombatGroupClients(partyId).SendAsync("GroupCombatFinished", snap);
            groupCombat.RemoveByParty(partyId);
            if (!encounter.CharactersWon)
            {
                logger.LogInformation("Group combat lost: {Chars}",
                    string.Join(", ", encounter.Characters.Select(c => c.Name)));
            }
            // Respawn every dead character regardless of whether the fight was won or lost -
            // CharactersWon only tracks whether the monsters died, not whether every party
            // member survived getting there (see GroupCombatEncounter.FinishCharactersWon,
            // which fires purely off Monsters.All(m => !m.IsAlive)). A party member who died
            // mid-fight while their ally finished the last monster off used to never reach
            // this call at all - stuck standing at 0 HP wherever the fight took place,
            // walking around the field but rejected from fighting again ("You cannot fight
            // while defeated"), since only the RespawnCharacterAsync this method calls
            // actually moves them to town, heals them, and notifies their client.
            // RespawnDeadGroupMembersAsync already only touches characters that are actually
            // dead, so calling it unconditionally is a no-op for a clean win.
            await RespawnDeadGroupMembersAsync(encounter);

            return snap;
        }

        // ── PvP (v0.3: arena rooms only, party vs party, rating + PvP currency stakes) ──

        /// <summary>Whether this connection is already in any kind of fight.</summary>
        private bool IsInAnyFight(string connId) =>
            groupCombat.GetPartyId(connId) is not null
            || session.GetEncounter(connId) is not null
            || pvp.IsInFight(connId);

        /// <summary>Members of <paramref name="partyId"/> standing in <paramref name="roomId"/> who
        /// can fight right now (alive, not already fighting).</summary>
        private List<(string Conn, Myria.Lib.Core.Entities.Characters.Character Character)> AvailablePartyMembersIn(string partyId, int roomId) =>
            party.GetMembers(partyId)
                .Select(m => presence.GetConnectionByName(m))
                .Where(c => c is not null && presence.GetRoom(c) == roomId && !IsInAnyFight(c))
                .Select(c => (Conn: c!, Character: session.Get(c!)))
                .Where(x => x.Character is { IsAlive: true })
                .Select(x => (x.Conn, x.Character!))
                .ToList();

        /// <summary>Everyone the caller could challenge in their current room - empty outside a PvP
        /// room. Party members are never listed (party members can't fight each other).</summary>
        public Task<List<PvpTargetInfo>> GetPvpTargets()
        {
            var result = new List<PvpTargetInfo>();
            if (session.Get(Context.ConnectionId) is not { } me) return Task.FromResult(result);
            if (presence.GetRoom(Context.ConnectionId) is not int roomId) return Task.FromResult(result);
            if (RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId) is not { IsPvpRoom: true }) return Task.FromResult(result);

            var myParty = party.GetPartyId(me.Name);
            foreach (var name in presence.GetCharactersInRoom(roomId))
            {
                if (string.Equals(name, me.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var theirParty = party.GetPartyId(name);
                if (myParty is not null && myParty == theirParty) continue;

                var conn = presence.GetConnectionByName(name);
                if (conn is null || IsInAnyFight(conn) || session.Get(conn) is not { IsAlive: true } them) continue;

                int partySize = theirParty is null ? 1 : AvailablePartyMembersIn(theirParty, roomId).Count;
                result.Add(new PvpTargetInfo(them.Name, them.Level, them.Class, theirParty, partySize));
            }
            return Task.FromResult(result);
        }

        /// <summary>
        /// Challenges <paramref name="targetName"/> in the caller's PvP room. There's no
        /// accept/decline (same immediacy as starting a monster fight). <paramref name="partyFight"/>
        /// pulls in every free party member present on both sides - an unpartied player is a party
        /// of one; otherwise it's a 1v1 between just the two of them.
        /// </summary>
        public async Task<StartPvpResult> StartPvpFight(string targetName, bool partyFight)
        {
            var caller = session.Get(Context.ConnectionId);
            if (caller is null) return new StartPvpResult(false, "failed", null);
            if (!caller.IsAlive) return new StartPvpResult(false, "player_dead", null);

            if (presence.GetRoom(Context.ConnectionId) is not int roomId
                || RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId) is not { IsPvpRoom: true })
                return new StartPvpResult(false, "not_pvp_room", null);

            if (IsInAnyFight(Context.ConnectionId)) return new StartPvpResult(false, "already_in_combat", null);
            if (string.Equals(targetName, caller.Name, StringComparison.OrdinalIgnoreCase))
                return new StartPvpResult(false, "failed", null);

            var targetConn = presence.GetConnectionByName(targetName);
            var target = targetConn is null ? null : session.Get(targetConn);
            if (targetConn is null || target is null || presence.GetRoom(targetConn) != roomId)
                return new StartPvpResult(false, "target_not_here", null);
            if (!target.IsAlive || IsInAnyFight(targetConn))
                return new StartPvpResult(false, "target_busy", null);

            var myParty = party.GetPartyId(caller.Name);
            var theirParty = party.GetPartyId(target.Name);
            if (myParty is not null && myParty == theirParty)
                return new StartPvpResult(false, "same_party", null);

            var sideA = partyFight && myParty is not null
                ? AvailablePartyMembersIn(myParty, roomId)
                : new() { (Context.ConnectionId, caller) };
            var sideB = partyFight && theirParty is not null
                ? AvailablePartyMembersIn(theirParty, roomId)
                : new() { (targetConn, target) };
            // The two principals must be in (AvailablePartyMembersIn only drops busy/dead/absent ones).
            if (!sideA.Any(x => x.Conn == Context.ConnectionId)) sideA.Insert(0, (Context.ConnectionId, caller));
            if (!sideB.Any(x => x.Conn == targetConn)) sideB.Insert(0, (targetConn, target));

            var encounter = new PvpEncounter(sideA.Select(x => x.Character), sideB.Select(x => x.Character));
            string fightId = $"pvp:{Guid.NewGuid():N}";
            if (!pvp.Start(fightId, sideA.Concat(sideB).Select(x => x.Conn).ToList(), encounter))
                return new StartPvpResult(false, "target_busy", null);

            var snap = BuildPvpSnapshot(encounter, 0);
            logger.LogInformation("PvP started (fight {FightId}, room {Room}): {A} vs {B}", fightId, roomId,
                string.Join(", ", sideA.Select(x => x.Character.Name)), string.Join(", ", sideB.Select(x => x.Character.Name)));

            await PvpClients(fightId).SendAsync("PvpStarted", snap);
            return new StartPvpResult(true, null, snap);
        }

        public Task<PvpSnapshot?> PvpAttack(int opponentIndex) =>
            RunPvpAction((enc, me) => enc.Attack(me, opponentIndex));

        /// <summary><paramref name="targetIndex"/>: an index into the opponents for enemy-targeted
        /// skills, into the caller's own side for SingleAlly.</summary>
        public Task<PvpSnapshot?> PvpCastSkill(string skillId, int targetIndex)
        {
            if (session.Get(Context.ConnectionId) is not { } player || ResolveCastableSkill(player, skillId) is not { } skill)
                return Task.FromResult<PvpSnapshot?>(null);
            return RunPvpAction((enc, me) => enc.CastSkill(me, skill, targetIndex));
        }

        public async Task<PvpSnapshot?> PvpUseItem(string itemId)
        {
            if (session.Get(Context.ConnectionId) is not { } player) return null;
            PvpSnapshot? snap;
            // Same per-character lock as every other inventory change.
            using (await locks.AcquireAsync(player.Name))
            {
                if (ResolveConsumable(player, itemId) is not { } item) return null;
                snap = await RunPvpAction((enc, me) => enc.UseItem(me, item));
            }
            if (snap is not null) await NotifyCharacterUpdate(player, inventory: true);
            return snap;
        }

        /// <summary>Forfeits the caller's current PvP fight (a loss, settled immediately).</summary>
        public Task PvpFlee() =>
            ForfeitPvpAsync(Context.ConnectionId, session.Get(Context.ConnectionId), notifyCaller: true);

        /// <summary>Any participant may call this; the encounter only skips the turn once it has
        /// actually run past PvpEncounter.TurnTimeout. Clients poll it while waiting.</summary>
        public async Task<bool> PvpSkipStalledTurn()
        {
            if (pvp.GetFightId(Context.ConnectionId) is not { } fightId || pvp.GetEncounter(fightId) is not { } enc)
                return false;
            int logBefore = enc.Log.Count;
            if (!enc.SkipStalledTurn()) return false;
            await CompletePvpActionAsync(fightId, enc, logBefore);
            return true;
        }

        /// <summary>
        /// Runs one turn action. A rejection that logged a reason (no mana, on cooldown, silenced)
        /// comes back to the caller only, so they see why; a silent rejection (not your turn, bad
        /// target) returns null and the client resyncs. Engine true = the turn was spent.
        /// </summary>
        private async Task<PvpSnapshot?> RunPvpAction(Func<PvpEncounter, string, bool> action)
        {
            if (pvp.GetFightId(Context.ConnectionId) is not { } fightId || pvp.GetEncounter(fightId) is not { } enc)
                return null;

            int logBefore = enc.Log.Count;
            if (!action(enc, DisplayName()))
                return enc.Log.Count > logBefore ? BuildPvpSnapshot(enc, logBefore) : null;

            return await CompletePvpActionAsync(fightId, enc, logBefore);
        }

        /// <summary>Broadcasts the turn, or - if it ended the fight - settles ratings, restores
        /// everyone to their pre-fight state, saves, and broadcasts the result.</summary>
        private async Task<PvpSnapshot> CompletePvpActionAsync(string fightId, PvpEncounter enc, int logBefore)
        {
            var snap = BuildPvpSnapshot(enc, logBefore);
            if (!enc.IsFinished)
            {
                await PvpClients(fightId).SendAsync("PvpUpdated", snap);
                return snap;
            }

            var losingSide = (enc.WinningSide == 1 ? enc.SideB : enc.SideA).ToList();
            var results = PvpRatingService.ApplyFightResult(enc.Winners, enc.Losers, losingSide);
            var settled = enc.Winners.Concat(enc.Losers).ToList(); // fled participants settled at flee time
            foreach (var c in settled) enc.RestorePreFightState(c);
            await SaveCombatParticipantsAsync(settled);
            foreach (var c in settled)
                if (session.GetConnectionId(c.Name) is string conn)
                    await NotifyCharacterUpdateFor(conn, c, vitals: true, pvpStats: true);

            snap = snap with { Results = results.Select(r => new PvpResultEntry(r.Name, r.Won, r.RatingDelta, r.NewRating, r.CurrencyDelta)).ToList() };
            logger.LogInformation("PvP finished (fight {FightId}): {Results}", fightId,
                string.Join(", ", results.Select(r => $"{r.Name} {(r.Won ? "won" : "lost")} {r.RatingDelta:+0;-0;0}")));

            // Broadcast before removing - PvpClients resolves recipients from the registry.
            await PvpClients(fightId).SendAsync("PvpFinished", snap);
            pvp.Remove(fightId);
            return snap;
        }

        /// <summary>
        /// Takes a participant out of their PvP fight as a forfeit: the loss is settled right away
        /// (they may be gone by the time the fight ends), they're restored to their pre-fight
        /// state and saved, and the rest of the fight carries on - or ends, if that was the last
        /// one on their side. Used by PvpFlee and by disconnects.
        /// </summary>
        private async Task ForfeitPvpAsync(string connId, Myria.Lib.Core.Entities.Characters.Character? player, bool notifyCaller)
        {
            if (player is null || pvp.GetFightId(connId) is not { } fightId || pvp.GetEncounter(fightId) is not { } enc)
                return;
            var me = enc.Find(player.Name);
            if (me is null) return;

            int logBefore = enc.Log.Count;
            if (!enc.Flee(me.Name)) return;

            var opponents = enc.OpponentsOf(me).Where(c => !enc.HasFled(c)).ToList();
            var result = PvpRatingService.ApplyForfeit(me, opponents.Count > 0 ? opponents : enc.OpponentsOf(me).ToList());
            enc.RestorePreFightState(me);
            pvp.RemoveConnection(connId);
            await SaveCombatParticipantsAsync(new[] { me });
            logger.LogInformation("PvP forfeit (fight {FightId}): {Name} {Delta:+0;-0;0}", fightId, me.Name, result.RatingDelta);

            if (notifyCaller)
            {
                await NotifyCharacterUpdate(me, vitals: true, pvpStats: true);
                await Clients.Caller.SendAsync("PvpFinished", BuildPvpSnapshot(enc, logBefore) with
                {
                    Finished = true,
                    Results = [new PvpResultEntry(result.Name, false, result.RatingDelta, result.NewRating, result.CurrencyDelta)]
                });
            }

            await CompletePvpActionAsync(fightId, enc, logBefore);
        }

        private static PvpSnapshot BuildPvpSnapshot(PvpEncounter enc, int logBefore)
        {
            GroupCombatantState State(Myria.Lib.Core.Entities.Characters.Character c) => new(
                c.Name, c.CurrentHealth, c.MaxHealth, enc.IsInFight(c), c.Level, Mana: c.CurrentMana, MaxMana: c.MaxMana);

            return new PvpSnapshot(
                enc.SideA.Select(State).ToList(),
                enc.SideB.Select(State).ToList(),
                enc.CurrentTurnCharacterName,
                enc.IsFinished,
                enc.WinningSide,
                enc.Log.Skip(logBefore)
                    .Select(e => new CombatLogMessage(e.Key, e.Args.Select(a => a?.ToString() ?? "").ToArray()))
                    .ToList());
        }

        private IClientProxy PvpClients(string fightId) => Clients.Clients(pvp.GetConnections(fightId));

        // ── Party ─────────────────────────────────────────────────────────────

        public async Task InviteToParty(string targetName)
        {
            var displayName = DisplayName();
            if (string.Equals(displayName, targetName, StringComparison.OrdinalIgnoreCase)) return;

            if (party.GetPartyId(targetName) is not null)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    $"{targetName} is already in a party.", "party");
                return;
            }

            var targetConn = presence.GetConnectionByName(targetName);
            if (targetConn is null)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    $"{targetName} is not online.", "party");
                return;
            }

            if (await IsBlockedByAsync(Context.User!.Identity!.Name!, targetName))
                return; // silently drop — blocked player's invite won't reach the blocker

            var (partyId, members, _) = party.GetOrCreate(displayName);

            if (members.Count >= PartyService.MaxSize)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System", "Your party is full.", "party");
                return;
            }

            // AddToGroupAsync is idempotent, so just always ensure the leader is in their own
            // party's group here rather than gating on members.Count == 1 - that guard silently
            // skipped this on a re-invite if the leader's connection had changed (e.g. reconnect)
            // since the party was first created, which is now also covered by LoadCharacter, but
            // there's no reason to leave this path fragile too.
            await Groups.AddToGroupAsync(Context.ConnectionId, PartyService.PartyGroup(partyId));

            // Only the invited character can use this invite (PartyService.AcceptInvite).
            party.Invite(partyId, presence.GetDisplayName(targetConn) ?? targetName);
            await Clients.Client(targetConn).SendAsync("PartyInvite", displayName, partyId);
            await Clients.Caller.SendAsync("ChatMessage", "System",
                $"Party invite sent to {targetName}.", "party");
        }

        public async Task AcceptPartyInvite(string partyId)
        {
            var displayName = DisplayName();
            var (success, members, leader) = party.AcceptInvite(partyId, displayName);

            if (!success)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "Could not join party — the invite may have expired, or the party is full or gone.", "party");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, PartyService.PartyGroup(partyId));
            await Clients.Group(PartyService.PartyGroup(partyId))
                .SendAsync("PartyUpdated", members.ToList(), leader);
            logger.LogInformation("{User} joined party {PartyId} (members: {Members})",
                displayName, partyId, string.Join(", ", members));
        }

        public async Task DeclinePartyInvite(string partyId, string fromName)
        {
            var displayName = DisplayName();
            party.DeclineInvite(partyId, displayName);
            var fromConn    = presence.GetConnectionByName(fromName);
            if (fromConn is not null)
                await Clients.Client(fromConn).SendAsync("ChatMessage", "System",
                    $"{displayName} declined your party invite.", "party");
        }

        public async Task LeaveParty()
        {
            await HandlePartyLeave(DisplayName(), notifyCaller: true);
        }

        public async Task KickFromParty(string targetName)
        {
            var displayName = DisplayName();
            var (success, remaining, disbanded, leader, partyId) = party.Kick(displayName, targetName);

            if (!success)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "Could not kick that player — you may not be the leader or they are not in the party.", "party");
                return;
            }

            logger.LogInformation("{User} kicked {Target} from party {PartyId}", displayName, targetName, partyId);

            var targetConn = presence.GetConnectionByName(targetName);
            if (targetConn is not null)
            {
                await Groups.RemoveFromGroupAsync(targetConn, PartyService.PartyGroup(partyId));
                await Clients.Client(targetConn).SendAsync("KickedFromParty");
            }

            if (disbanded)
            {
                if (remaining.Count == 1)
                {
                    var lastConn = presence.GetConnectionByName(remaining[0]);
                    if (lastConn is not null)
                    {
                        await Groups.RemoveFromGroupAsync(lastConn, PartyService.PartyGroup(partyId));
                        await Clients.Client(lastConn).SendAsync("PartyDisbanded");
                    }
                }
            }
            else
            {
                await Clients.Group(PartyService.PartyGroup(partyId))
                    .SendAsync("PartyUpdated", remaining.ToList(), leader);
                await Clients.Group(PartyService.PartyGroup(partyId))
                    .SendAsync("ChatMessage", "System", $"{targetName} was kicked from the party.", "party");
            }
        }

        public async Task TransferPartyLeader(string targetName)
        {
            var displayName = DisplayName();
            var (success, members, newLeader) = party.TransferLeader(displayName, targetName);

            if (!success)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "Could not transfer leadership — you may not be the leader or that player is not in the party.", "party");
                return;
            }

            var partyId = party.GetPartyId(displayName);
            if (partyId is null) return;

            await Clients.Group(PartyService.PartyGroup(partyId))
                .SendAsync("PartyUpdated", members.ToList(), newLeader);
            await Clients.Group(PartyService.PartyGroup(partyId))
                .SendAsync("ChatMessage", "System",
                    $"{displayName} transferred party leadership to {newLeader}.", "party");
        }

        public async Task UpdatePartyStats()
        {
            var displayName = DisplayName();
            var partyId     = party.GetPartyId(displayName);
            if (partyId is null) return;
            var player = session.Get(Context.ConnectionId);
            if (player is null) return;
            await Clients.OthersInGroup(PartyService.PartyGroup(partyId))
                .SendAsync("PartyMemberStats", displayName,
                    player.CurrentHealth, player.MaxHealth,
                    player.CurrentMana,   player.MaxMana,
                    player.Level);
        }

        // ── Trade ─────────────────────────────────────────────────────────────

        public async Task ProposeTrade(string targetName)
        {
            var displayName = DisplayName();
            if (string.Equals(displayName, targetName, StringComparison.OrdinalIgnoreCase)) return;

            var targetConn = presence.GetConnectionByName(targetName);
            if (targetConn is null)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    $"{targetName} is not online.", "room");
                return;
            }

            if (presence.GetRoom(Context.ConnectionId) != presence.GetRoom(targetConn))
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    $"{targetName} is not in the same room.", "room");
                return;
            }

            // Bind the proposal to the target's actual in-game name (not the client's spelling of it).
            var targetDisplayName = presence.GetDisplayName(targetConn) ?? targetName;
            var (tradeId, ok) = trade.Propose(Context.ConnectionId, displayName, targetDisplayName);
            if (!ok)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "You are already in a trade.", "room");
                return;
            }

            await Clients.Client(targetConn).SendAsync("TradeProposed", displayName, tradeId);
            await Clients.Caller.SendAsync("ChatMessage", "System",
                $"Trade offer sent to {targetName}.", "room");
        }

        public async Task DeclineTrade(string tradeId)
        {
            trade.CancelPending(tradeId, DisplayName());
            await Clients.Caller.SendAsync("ChatMessage", "System", "Trade declined.", "room");
        }

        public async Task AcceptTrade(string tradeId)
        {
            var displayName = DisplayName();
            var (ok, proposerConn, proposerName, activeId) = trade.Accept(
                Context.ConnectionId, displayName, tradeId);

            if (!ok)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "Could not accept trade — it may have expired.", "room");
                return;
            }

            await Clients.Client(proposerConn).SendAsync("TradeStarted", displayName);
            await Clients.Caller.SendAsync("TradeStarted", proposerName);
        }

        public async Task AddTradeItem(string itemId, int quantity)
        {
            var player = session.Get(Context.ConnectionId);
            if (player is null) return;

            var item = player.Inventory.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || item.StackSize < quantity) return;

            if (!trade.AddItem(Context.ConnectionId, itemId, item.Name, quantity)) return;

            await BroadcastTradeSnapshot();
        }

        public async Task RemoveTradeItem(string itemId)
        {
            if (!trade.RemoveItem(Context.ConnectionId, itemId)) return;
            await BroadcastTradeSnapshot();
        }

        public async Task SetTradeGold(long amount)
        {
            if (amount < 0) return;
            var player = session.Get(Context.ConnectionId);
            if (player is null || !player.Money.CanAfford(amount)) return;

            if (!trade.SetGold(Context.ConnectionId, amount)) return;
            await BroadcastTradeSnapshot();
        }

        public async Task ConfirmTrade()
        {
            var player = session.Get(Context.ConnectionId);
            if (player is null) return;

            var (ok, bothReady) = trade.Confirm(Context.ConnectionId);
            if (!ok) return;

            if (!bothReady)
            {
                await BroadcastTradeSnapshot();
                return;
            }

            // Both characters are locked for the whole swap, and the trade is claimed atomically - an
            // offer change or a second ConfirmTrade can't slip in (security review 2026-11-26, A06 F10).
            var peekConn = trade.PartnerConn(Context.ConnectionId);
            var peekPartner = peekConn is null ? null : session.Get(peekConn);
            using var held = await locks.AcquireAsync(player.Name, peekPartner?.Name);

            if (!trade.TryClaimCompleted(Context.ConnectionId, out var snap, out var partnerConn) || snap is null)
                return; // offer changed meanwhile (both flags reset) or the partner's call already executed it
            var partner = session.Get(partnerConn!);
            if (partner is null || !ReferenceEquals(partner, peekPartner))
            { await NotifyTradeCancelledAsync(partnerConn, "partner_offline"); return; }

            if (TradeProblem(player, partner, snap) is { } problem)
            { await NotifyTradeCancelledAsync(partnerConn, problem); return; }

            // Transfer items: mine → partner, theirs → me (space checked above, so every add succeeds)
            TransferItems(player, partner, snap.MyItems);
            TransferItems(partner, player, snap.TheirItems);

            // Gold moves 1:1. It used to arrive multiplied by the receiver's fame bonus (up to x2.5),
            // so two accounts passing gold back and forth created gold (A06 F7). The fame bonus
            // belongs to selling goods to NPCs, not to gifts between players.
            if (snap.MyGold > 0 && player.Money.TrySpend(snap.MyGold)) partner.Money.TryAdd(snap.MyGold);
            if (snap.TheirGold > 0 && partner.Money.TrySpend(snap.TheirGold)) player.Money.TryAdd(snap.TheirGold);

            logger.LogInformation("Trade completed between {A} and {B} ({AGold}/{BGold} gold, {AItems}/{BItems} item stacks)",
                player.Name, partner.Name, snap.MyGold, snap.TheirGold, snap.MyItems.Count, snap.TheirItems.Count);

            // Both sides persisted right away: an unsaved side could otherwise be dropped (character
            // switch, crash) while the other keeps what it received - duplication (A06 F6).
            await SaveLiveCharactersAsync((player, Context.ConnectionId), (partner, partnerConn!));

            // Both sides' inventory/gold changed - each connection only gets its own update.
            await NotifyCharacterUpdate(player, inventory: true, money: true);
            await NotifyCharacterUpdateFor(partnerConn!, partner, inventory: true, money: true);

            await Clients.Client(Context.ConnectionId).SendAsync("TradeCompleted");
            await Clients.Client(partnerConn!).SendAsync("TradeCompleted");
        }

        // Re-checks a claimed trade against both live characters: items and gold still there, and
        // everything fits on the receiving side (inventory slots and money capacity).
        private static string? TradeProblem(
            Myria.Lib.Core.Entities.Characters.Character me,
            Myria.Lib.Core.Entities.Characters.Character them,
            TradeSnapshot snap)
        {
            static bool Owns(Myria.Lib.Core.Entities.Characters.Character c, List<TradeItem> items) =>
                items.All(o => o.Quantity > 0 &&
                               c.Inventory.Items.Where(i => i.Id == o.ItemId).Sum(i => (long)i.StackSize) >= o.Quantity);

            if (!Owns(me, snap.MyItems) || !Owns(them, snap.TheirItems)) return "item_not_available";
            if (!me.Money.CanAfford(snap.MyGold) || !them.Money.CanAfford(snap.TheirGold)) return "gold_not_available";

            // Money capacity after the own payment leaves.
            static bool CanReceive(Myria.Lib.Core.Entities.Characters.Character c, long pays, long gets) =>
                c.Money.IsUnlimited || gets <= c.Money.Capacity - (c.Money.Balance.BronzeTotal - pays);
            if (!CanReceive(me, snap.MyGold, snap.TheirGold) || !CanReceive(them, snap.TheirGold, snap.MyGold))
                return "gold_capacity";

            if (!FitsAll(them, snap.MyItems) || !FitsAll(me, snap.TheirItems)) return "inventory_full";
            return null;
        }

        // Whether all these items fit together (not just each one on its own): space in matching
        // stacks first, then the new slots they'd need, against the free slots.
        private static bool FitsAll(Myria.Lib.Core.Entities.Characters.Character c, IEnumerable<TradeItem> items)
        {
            long slotsNeeded = 0;
            foreach (var o in items)
            {
                if (!Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(o.ItemId, out var template) || template is null)
                    return false;
                long left = o.Quantity - c.Inventory.Items.Where(e => e.CanStackWith(template))
                                         .Sum(e => (long)Math.Max(0, e.MaxStackSize - e.StackSize));
                if (left > 0)
                {
                    int max = Math.Max(1, template.MaxStackSize);
                    slotsNeeded += (left + max - 1) / max;
                }
            }
            return slotsNeeded <= c.Inventory.Capacity - c.Inventory.Items.Count;
        }

        private async Task NotifyTradeCancelledAsync(string? partnerConn, string reason)
        {
            await Clients.Caller.SendAsync("TradeCancelled", reason);
            if (!string.IsNullOrEmpty(partnerConn) && partnerConn != Context.ConnectionId)
                await Clients.Client(partnerConn).SendAsync("TradeCancelled", reason);
        }

        // Saves live session characters right away (each under its own account), e.g. both sides of a
        // trade or a shop sale. Best effort with retries, like every other save path.
        private async Task SaveLiveCharactersAsync(
            params (Myria.Lib.Core.Entities.Characters.Character Character, string Conn)[] characters)
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            foreach (var (character, conn) in characters)
            {
                var username = presence.GetAccountUsername(conn);
                if (username is not null)
                    await SaveCharacterReliablyAsync(repo, username, character, "exchange");
            }
        }

        public async Task CancelTrade()
            => await CancelTradeInternal("cancelled");

        private async Task CancelTradeInternal(string reason)
        {
            var (connA, connB) = trade.End(Context.ConnectionId);
            if (!string.IsNullOrEmpty(connA))
                await Clients.Client(connA).SendAsync("TradeCancelled", reason);
            if (!string.IsNullOrEmpty(connB) && connB != connA)
                await Clients.Client(connB).SendAsync("TradeCancelled", reason);
        }

        private async Task BroadcastTradeSnapshot()
        {
            var partnerConn = trade.PartnerConn(Context.ConnectionId);
            if (partnerConn is null) return;

            var mySnap      = trade.Snapshot(Context.ConnectionId)!;
            var partnerSnap = trade.Snapshot(partnerConn)!;

            await Clients.Client(Context.ConnectionId).SendAsync("TradeUpdated", mySnap);
            await Clients.Client(partnerConn).SendAsync("TradeUpdated", partnerSnap);
        }

        private static void TransferItems(
            Myria.Lib.Core.Entities.Characters.Character from,
            Myria.Lib.Core.Entities.Characters.Character to,
            List<TradeItem> items)
        {
            foreach (var offer in items)
            {
                int remaining = offer.Quantity;
                foreach (var stack in from.Inventory.Items
                    .Where(i => i.Id == offer.ItemId).ToList())
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(stack.StackSize, remaining);
                    stack.StackSize -= take;
                    remaining -= take;
                    if (stack.StackSize <= 0) from.Inventory.RemoveItem(stack);

                    if (Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(offer.ItemId, out var copy))
                    {
                        copy.StackSize = take;
                        to.Inventory.AddItem(copy, to);
                    }
                }
            }
        }

        // ── Player Shop ───────────────────────────────────────────────────────────
        // See PlayerShopService for the persistence/visibility model. One shop per character,
        // anchored to the room it was opened in; visible there while the owner is online and
        // either physically present or has a Merchant's Seal in storage.

        public async Task OpenShop()
        {
            var displayName = DisplayName();
            if (presence.GetRoom(Context.ConnectionId) is not int rid) return;

            // Authoritative check - the client only uses this to enable/disable its own button,
            // it doesn't get to decide on its own.
            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == rid);
            if (room is null || CityRegistry.GetCityByRoom(room) is null)
            {
                await Clients.Caller.SendAsync("ShopError", "not_a_city_room");
                return;
            }

            var (ok, error, _) = await shop.OpenShopAsync(displayName, rid);
            if (!ok)
            {
                await Clients.Caller.SendAsync("ShopError", error);
                return;
            }
            await Clients.Group(RoomGroup(rid)).SendAsync("ShopOpened", displayName);
            await Clients.Caller.SendAsync("MyShopUpdated", new List<PlayerShopItemDto>());
        }

        // Best-effort returns everything in storage to the owner's live inventory before the
        // shop row is deleted - there's no separate "collect proceeds/items" step in this design.
        public async Task CloseShop()
        {
            var displayName = DisplayName();
            var player = session.Get(Context.ConnectionId);
            if (player is null) return;   // stock goes back into the live character, so one is needed

            // Locked like a purchase from this shop (which locks the owner too), so no sale can land
            // between reading the stock and deleting the shop (A06 F10).
            using var held = await locks.AcquireAsync(player.Name);
            var existing = await shop.GetShopByOwnerNameAsync(displayName);
            if (existing is null) return;

            // Everything must fit, or nothing is closed - items used to vanish on a full inventory (A06 F14).
            var stock = existing.Items.Select(s => new TradeItem(s.ItemId, s.ItemId, s.Quantity)).ToList();
            if (!FitsAll(player, stock))
            {
                await Clients.Caller.SendAsync("ShopError", "inventory_full");
                return;
            }

            var roomId = await shop.CloseShopAsync(displayName);
            if (roomId is null) return;

            foreach (var stored in stock)
            {
                if (!Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(stored.ItemId, out var restored) || restored is null)
                    continue;
                restored.StackSize = stored.Quantity;
                player.Inventory.AddItem(restored, player);
            }
            await SaveLiveCharactersAsync((player, Context.ConnectionId));
            await NotifyCharacterUpdate(player, inventory: true);

            await Clients.Group(RoomGroup(roomId.Value)).SendAsync("ShopClosed", displayName);
        }

        public async Task<List<PlayerShopItemDto>> GetMyShop()
        {
            var existing = await shop.GetShopByOwnerNameAsync(DisplayName());
            return existing?.Items.Select(i => new PlayerShopItemDto(i.ItemId, i.Quantity, i.Price)).ToList()
                ?? new List<PlayerShopItemDto>();
        }

        public async Task<bool> DepositShopItem(string itemId, int quantity)
        {
            if (quantity <= 0) return false;
            var player = session.Get(Context.ConnectionId);
            if (player is null) return false;

            // Locked so a trade can't move the item away while the shop write is awaited (A06 F10).
            using var held = await locks.AcquireAsync(player.Name);
            int owned = player.Inventory.Items.Where(i => i.Id == itemId).Sum(i => i.StackSize);
            if (owned < quantity)
            {
                await Clients.Caller.SendAsync("ShopError", "not_owned");
                return false;
            }

            var displayName  = DisplayName();
            var beforeShop   = await shop.GetShopByOwnerNameAsync(displayName);
            if (beforeShop is null)
            {
                await Clients.Caller.SendAsync("ShopError", "no_shop");
                return false;
            }
            // Depositing the seal while away from the shop's room can flip it from invisible to
            // visible - only relevant if it wasn't already sealed (a second seal changes nothing).
            bool mayRevealShop = itemId == PlayerShopService.SealItemId
                && !PlayerShopService.HasSeal(beforeShop)
                && presence.GetRoom(Context.ConnectionId) != beforeShop.RoomId;

            // Commit to shop storage BEFORE touching the player's live inventory - otherwise a
            // failure here (e.g. storage_full) would have already removed the item with nothing
            // to show for it. DepositAsync doesn't touch the inventory itself, so this ordering
            // costs nothing.
            var (ok, error, items) = await shop.DepositAsync(displayName, itemId, quantity);
            if (!ok || items is null)
            {
                await Clients.Caller.SendAsync("ShopError", error);
                return false;
            }

            int remaining = quantity;
            foreach (var stack in player.Inventory.Items.Where(i => i.Id == itemId).ToList())
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, stack.StackSize);
                if (stack.StackSize == take) player.Inventory.RemoveItem(stack);
                else stack.StackSize -= take;
                remaining -= take;
            }

            // The shop row is already in the database - the inventory must be too, or a character
            // switch would bring the deposited items back (A06 F6).
            await SaveLiveCharactersAsync((player, Context.ConnectionId));
            await NotifyCharacterUpdate(player, inventory: true);
            await Clients.Caller.SendAsync("MyShopUpdated", items);

            if (mayRevealShop)
                await Clients.Group(RoomGroup(beforeShop.RoomId)).SendAsync("ShopOpened", displayName);

            return true;
        }

        public async Task<int> WithdrawShopItem(string itemId, int quantity)
        {
            if (quantity <= 0) return 0;
            var player = session.Get(Context.ConnectionId);
            if (player is null) return 0;

            using var held = await locks.AcquireAsync(player.Name);
            var displayName = DisplayName();
            var beforeShop   = await shop.GetShopByOwnerNameAsync(displayName);
            if (beforeShop is null) return 0;

            // Check space before anything leaves storage (items used to vanish on a full inventory, A06 F14).
            int canTake = Math.Min(quantity, beforeShop.Items.FirstOrDefault(i => i.ItemId == itemId)?.Quantity ?? 0);
            if (canTake > 0 && !FitsAll(player, [new TradeItem(itemId, itemId, canTake)]))
            {
                await Clients.Caller.SendAsync("ShopError", "inventory_full");
                return 0;
            }

            // Withdrawing the last seal while away from the shop's room hides it again.
            bool mayHideShop = itemId == PlayerShopService.SealItemId
                && presence.GetRoom(Context.ConnectionId) != beforeShop.RoomId
                && beforeShop.Items.SingleOrDefault(i => i.ItemId == PlayerShopService.SealItemId)
                    is { } sealItem && sealItem.Quantity <= quantity;

            var (ok, error, withdrawn, items) = await shop.WithdrawAsync(displayName, itemId, quantity);
            if (!ok || items is null)
            {
                await Clients.Caller.SendAsync("ShopError", error);
                return 0;
            }

            if (withdrawn > 0 &&
                Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(itemId, out var restored) && restored is not null)
            {
                restored.StackSize = withdrawn;
                player.Inventory.AddItem(restored, player);
            }

            await SaveLiveCharactersAsync((player, Context.ConnectionId));
            await NotifyCharacterUpdate(player, inventory: true);
            await Clients.Caller.SendAsync("MyShopUpdated", items);

            if (mayHideShop)
                await Clients.Group(RoomGroup(beforeShop.RoomId)).SendAsync("ShopClosed", displayName);

            return withdrawn;
        }

        public async Task SetShopItemPrice(string itemId, long price)
        {
            var (ok, error, items) = await shop.SetPriceAsync(DisplayName(), itemId, price);
            if (!ok || items is null) { await Clients.Caller.SendAsync("ShopError", error); return; }
            await Clients.Caller.SendAsync("MyShopUpdated", items);
        }

        public async Task UnlistShopItem(string itemId)
        {
            var (ok, error, items) = await shop.SetPriceAsync(DisplayName(), itemId, null);
            if (!ok || items is null) { await Clients.Caller.SendAsync("ShopError", error); return; }
            await Clients.Caller.SendAsync("MyShopUpdated", items);
        }

        // Only priced items - a buyer never sees what's merely stored (incl. the Seal, which is
        // never priceable - see PlayerShopService.SetPriceAsync).
        public async Task<List<PlayerShopItemDto>> BrowseShop(string ownerName)
        {
            var existing = await shop.GetShopByOwnerNameAsync(ownerName);
            return existing?.Items.Where(i => i.Price is not null)
                .Select(i => new PlayerShopItemDto(i.ItemId, i.Quantity, i.Price)).ToList()
                ?? new List<PlayerShopItemDto>();
        }

        public async Task BuyFromShop(string ownerName, string itemId, int qty)
        {
            if (qty <= 0) return;
            var buyerName = DisplayName();
            if (string.Equals(buyerName, ownerName, StringComparison.OrdinalIgnoreCase)) return;

            var buyer = session.Get(Context.ConnectionId);
            if (buyer is null) return;

            // Buyer and seller locked for the whole purchase: no trade or second purchase can move the
            // buyer's gold or the seller's stock in between (A06 F10). The seller's name is the shop's.
            using var held = await locks.AcquireAsync(buyer.Name, ownerName);

            var (item, state) = await shop.ValidatePurchaseAsync(ownerName, itemId, qty);
            if (item is null)
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false,
                    state.Visible ? "no_listing" : "shop_closed", itemId, 0, 0L);
                return;
            }

            // Price capped (also for listings priced before the cap existed) and the total computed
            // without overflow: an overflowed total used to become a huge credit (A06 F2).
            long unitPrice = item.Price ?? 0;
            if (unitPrice <= 0 || unitPrice > PlayerShopService.MaxUnitPrice)
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "no_listing", itemId, 0, 0L);
                return;
            }
            long totalCost = unitPrice * qty;   // <= 10^9 * int.MaxValue, far below long.MaxValue

            if (!buyer.Money.CanAfford(totalCost))
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "insufficient_gold", itemId, 0, 0L);
                return;
            }

            if (!Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(itemId, out var newItem) || newItem is null)
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "item_unavailable", itemId, 0, 0L);
                return;
            }
            if (!buyer.Inventory.CanAdd(newItem, qty))
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "inventory_full", itemId, 0, 0L);
                return;
            }

            // Pay first; the result counts (it used to be ignored). Refund if the stock is gone.
            if (!buyer.Money.TrySpend(totalCost))
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "insufficient_gold", itemId, 0, 0L);
                return;
            }
            bool sold;
            try
            {
                sold = await shop.CommitSaleAsync(ownerName, itemId, qty);
            }
            catch
            {
                // Paid already: any database error here (not only "sold out") must give the gold back
                // before the error propagates (security review 2026-11-26, A10 F2).
                buyer.Money.TryAdd(totalCost);
                throw;
            }
            if (!sold)
            {
                buyer.Money.TryAdd(totalCost);
                await Clients.Caller.SendAsync("ShopBuyResult", false, "insufficient_stock", itemId, 0, 0L);
                return;
            }

            newItem.StackSize = qty;
            buyer.Inventory.AddItem(newItem, buyer);   // space checked above, under the lock

            long fee = state.Remote
                ? (long)Math.Round(totalCost * PlayerShopService.RemoteSaleFeeRate, MidpointRounding.AwayFromZero)
                : 0;
            long payout = totalCost - fee;

            // The seller is guaranteed online here (ValidatePurchaseAsync required state.Visible,
            // which requires it) - GetConnectionByName not resolving would only mean a race with
            // a near-simultaneous disconnect, in which case the payout is simply not delivered
            // rather than queued/escrowed. An accepted gap at this scale, same trade-off already
            // made elsewhere in this codebase (see AuthService.DeleteAccountAsync's remarks).
            var sellerConn = presence.GetConnectionByName(ownerName);
            var seller      = sellerConn is not null ? session.Get(sellerConn) : null;
            seller?.Money.TryAdd(payout);

            // Stock is already sold in the database - both characters must be saved now (A06 F6).
            if (seller is not null && sellerConn is not null)
                await SaveLiveCharactersAsync((buyer, Context.ConnectionId), (seller, sellerConn));
            else
                await SaveLiveCharactersAsync((buyer, Context.ConnectionId));
            if (sellerConn is not null)
                await Clients.Client(sellerConn).SendAsync("ShopSale", buyerName, itemId, qty, totalCost, fee);

            logger.LogInformation("{Buyer} bought {Qty}x {Item} from {Seller} for {Cost} gold (fee {Fee})",
                buyerName, qty, itemId, ownerName, totalCost, fee);
            await Clients.Caller.SendAsync("ShopBuyResult", true, "", itemId, qty, totalCost);
        }

        // ── Guild ─────────────────────────────────────────────────────────────

        /// <summary>Sends a guild invite to an online or offline character.</summary>
        public async Task GuildSendInvite(string targetName, bool asRookie)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId = await GetCharacterIdByNameAsync(db, DisplayName());
            var (success, error, inviteId, _, guildId) =
                await svc.InviteAsync(myId, targetName, asRookie);

            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            var targetConn = presence.GetConnectionByName(targetName);
            if (targetConn is not null)
            {
                var guild = await db.Guilds.FindAsync(guildId);
                await Clients.Client(targetConn).SendAsync(
                    "GuildInviteReceived", inviteId, guildId, guild?.Name, guild?.Tag, asRookie);
            }

            await Clients.Caller.SendAsync("ChatMessage", "System",
                $"Guild invite sent to {targetName}.", "guild");
        }

        /// <summary>Accepts a pending guild invite, joining as member or rookie.</summary>
        public async Task GuildAcceptInvite(int inviteId)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myName = DisplayName();
            var myId   = await GetCharacterIdByNameAsync(db, myName);

            var (success, error, guildId, joinedAsRookie) = await svc.AcceptInviteAsync(myId, inviteId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            var rank = joinedAsRookie ? "Rookie" : "Member";
            await Groups.AddToGroupAsync(Context.ConnectionId, GuildService.GuildGroup(guildId));
            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildMemberJoined", myName, rank);
            logger.LogInformation("{User} joined guild {GuildId} as {Rank}", myName, guildId, rank);
        }

        /// <summary>Declines a pending invite.</summary>
        public async Task GuildDeclineInvite(int inviteId)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId = await GetCharacterIdByNameAsync(db, DisplayName());
            await svc.DeclineInviteAsync(myId, inviteId);
        }

        /// <summary>Leaves the guild voluntarily. The leader must transfer or disband first.</summary>
        public async Task GuildLeave()
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myName = DisplayName();
            var myId   = await GetCharacterIdByNameAsync(db, myName);

            var (success, error, guildId) = await svc.LeaveGuildAsync(myId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GuildService.GuildGroup(guildId));
            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildMemberLeft", myName, false);
            await Clients.Caller.SendAsync("GuildLeft");
            logger.LogInformation("{User} left guild {GuildId}", myName, guildId);
        }

        /// <summary>Kicks a member from the guild (Officer+, cannot kick equal or higher rank).</summary>
        public async Task GuildKick(string targetName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId     = await GetCharacterIdByNameAsync(db, DisplayName());
            var targetId = await GetCharacterIdByNameAsync(db, targetName);

            var (success, error, _, guildId) = await svc.KickMemberAsync(myId, targetId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            var targetConn = presence.GetConnectionByName(targetName);
            if (targetConn is not null)
            {
                await Groups.RemoveFromGroupAsync(targetConn, GuildService.GuildGroup(guildId));
                await Clients.Client(targetConn).SendAsync("GuildKicked");
            }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildMemberLeft", targetName, true);
            logger.LogInformation("{User} kicked {Target} from guild {GuildId}", DisplayName(), targetName, guildId);
        }

        /// <summary>Promotes a member one rank. Officers can promote up to Elite; Leader can promote to any rank below Leader.</summary>
        public async Task GuildPromote(string targetName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId     = await GetCharacterIdByNameAsync(db, DisplayName());
            var targetId = await GetCharacterIdByNameAsync(db, targetName);

            var (success, error, _, newRank, _, guildId) = await svc.PromoteMemberAsync(myId, targetId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildRankChanged", targetName, newRank.ToString());
        }

        /// <summary>Demotes a member one rank (Leader only).</summary>
        public async Task GuildDemote(string targetName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId     = await GetCharacterIdByNameAsync(db, DisplayName());
            var targetId = await GetCharacterIdByNameAsync(db, targetName);

            var (success, error, _, newRank, _, guildId) = await svc.DemoteMemberAsync(myId, targetId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildRankChanged", targetName, newRank.ToString());
        }

        /// <summary>Transfers leadership. Caller drops to Officer; target becomes Leader.</summary>
        public async Task GuildTransferLeadership(string targetName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myName   = DisplayName();
            var myId     = await GetCharacterIdByNameAsync(db, myName);
            var targetId = await GetCharacterIdByNameAsync(db, targetName);

            var (success, error, guildId) = await svc.TransferLeadershipAsync(myId, targetId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildLeaderChanged", myName, targetName);
        }

        /// <summary>Hires a character as a guild rookie directly (Elite+). The rookie is notified if online.</summary>
        public async Task GuildHireRookie(string rookieName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId = await GetCharacterIdByNameAsync(db, DisplayName());
            var (success, error, _, guildId) = await svc.HireRookieAsync(myId, rookieName);

            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            var rookieConn = presence.GetConnectionByName(rookieName);
            if (rookieConn is not null)
            {
                var guild = await db.Guilds.FindAsync(guildId);
                await Clients.Client(rookieConn).SendAsync(
                    "GuildRookieHired", guildId, guild?.Name, guild?.Tag);
                await Groups.AddToGroupAsync(rookieConn, GuildService.GuildGroup(guildId));
            }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildRookieAdded", rookieName);
        }

        /// <summary>Promotes a rookie to a full Member (Officer+).</summary>
        public async Task GuildPromoteRookie(string rookieName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId     = await GetCharacterIdByNameAsync(db, DisplayName());
            var rookieId = await GetCharacterIdByNameAsync(db, rookieName);

            var (success, error, _, guildId) = await svc.PromoteRookieToMemberAsync(myId, rookieId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            // Rookie stays in the guild group — just their rank changes in clients' view
            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildMemberJoined", rookieName, GuildRank.Member.ToString());
        }

        /// <summary>Removes a rookie from the guild program (Elite+).</summary>
        public async Task GuildFireRookie(string rookieName)
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId     = await GetCharacterIdByNameAsync(db, DisplayName());
            var rookieId = await GetCharacterIdByNameAsync(db, rookieName);

            var (success, error, _, guildId) = await svc.FireRookieAsync(myId, rookieId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            var rookieConn = presence.GetConnectionByName(rookieName);
            if (rookieConn is not null)
            {
                await Groups.RemoveFromGroupAsync(rookieConn, GuildService.GuildGroup(guildId));
                await Clients.Client(rookieConn).SendAsync("GuildKicked");
            }

            await Clients.Group(GuildService.GuildGroup(guildId))
                .SendAsync("GuildMemberLeft", rookieName, true);
        }

        /// <summary>Disbands the guild (Leader only). Notifies all online members before deleting.</summary>
        public async Task GuildDisband()
        {
            using var scope = scopeFactory.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<GuildService>();

            var myId       = await GetCharacterIdByNameAsync(db, DisplayName());
            var membership = await db.GuildMembers.FirstOrDefaultAsync(m => m.CharacterId == myId);
            if (membership is null || membership.Rank != GuildRank.Leader)
            {
                await Clients.Caller.SendAsync("GuildError", "Only the guild leader can disband the guild.");
                return;
            }

            var guildId = membership.GuildId;

            // Collect all names before the cascade delete removes them
            var allNames = await db.GuildMembers
                .Where(m => m.GuildId == guildId)
                .Join(db.Characters, m => m.CharacterId, c => c.Id, (_, c) => c.Name)
                .ToListAsync();
            var rookieNames = await db.GuildRookies
                .Where(r => r.GuildId == guildId)
                .Join(db.Characters, r => r.CharacterId, c => c.Id, (_, c) => c.Name)
                .ToListAsync();

            var (success, error, _, _) = await svc.DisbandGuildAsync(myId);
            if (!success) { await Clients.Caller.SendAsync("GuildError", error); return; }

            logger.LogInformation("{User} disbanded guild {GuildId}", DisplayName(), guildId);
            await Clients.Group(GuildService.GuildGroup(guildId)).SendAsync("GuildDisbanded");

            foreach (var name in allNames.Concat(rookieNames))
            {
                var conn = presence.GetConnectionByName(name);
                if (conn is not null)
                    await Groups.RemoveFromGroupAsync(conn, GuildService.GuildGroup(guildId));
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // "Who is calling": the ownership-verified loaded character first (defence in depth -
        // see SetCharacterName), then the presence name, then the account.
        private string DisplayName() =>
            session.Get(Context.ConnectionId)?.Name
            ?? presence.GetDisplayName(Context.ConnectionId)
            ?? Context.User!.Identity!.Name!;

        private static List<GroupCombatantState> BuildCharacterStates(GroupCombatEncounter enc) =>
            enc.Characters.Select(p => new GroupCombatantState(
                p.Name, p.CurrentHealth, p.MaxHealth, p.IsAlive,
                XpGained: enc.XpGrantedByCharacter.GetValueOrDefault(p),
                LootItemIds: enc.LootGrantedByCharacter.GetValueOrDefault(p),
                Mana: p.CurrentMana,
                MaxMana: p.MaxMana)).ToList();

        private static List<GroupCombatantState> BuildMonsterStates(GroupCombatEncounter enc) =>
            enc.Monsters.Select(m => new GroupCombatantState(
                m.Name, m.CurrentHealth, m.MaxHealth, m.IsAlive, m.Level,
                Mana: m.CurrentMana,
                MaxMana: m.MaxMana)).ToList();

        private static GroupCombatSnapshot BuildGroupSnapshot(GroupCombatEncounter enc, int logBefore)
        {
            var newLogs = enc.Log.Skip(logBefore)
                .Select(e => new CombatLogMessage(e.Key, e.Args.Select(a => a?.ToString() ?? "").ToArray()))
                .ToList();
            return new GroupCombatSnapshot(
                BuildCharacterStates(enc),
                BuildMonsterStates(enc),
                enc.CurrentTurnCharacterName,
                enc.IsFinished,
                enc.CharactersWon,
                newLogs);
        }

        private static GroupCombatSnapshot EmptyGroupSnapshot() =>
            new([], [], "", false, false, []);

        private async Task<CombatTurnResult> RunCombatAction(Action<CombatEncounter> action)
        {
            var player   = session.Get(Context.ConnectionId);
            var encounter = session.GetEncounter(Context.ConnectionId);
            if (player == null || encounter == null || !player.IsAlive)
                return EmptyCombatTurn();

            int logBefore = encounter.Log.Count;
            action(encounter);

            var newLogs = encounter.Log.Skip(logBefore)
                .Select(e => new CombatLogMessage(
                    e.Key,
                    e.Args.Select(a => a?.ToString() ?? "").ToArray()))
                .ToList();

            bool finished = encounter.Phase == CombatPhase.Finished;
            bool won      = finished && player.IsAlive;

            List<string> loot = [];
            long xp = 0;
            if (finished)
            {
                loot = encounter.GetDropNames().Keys.ToList();
                xp   = won ? encounter.LastXpGained : 0;
                session.RemoveEncounter(Context.ConnectionId);

                if (presence.GetRoom(Context.ConnectionId) is int combatRoomId)
                    await Clients.OthersInGroup(RoomGroup(combatRoomId)).SendAsync("CharacterCombatEnded", DisplayName());

                if (won)
                {
                    logger.LogInformation("{Character} won a solo fight (+{Xp} XP, now Level {Level})",
                        player.Name, xp, player.Level);
                    // CombatTurnResult already carries HP/loot ids for the immediate turn - this
                    // is the reconciling push for everything that isn't per-turn (items actually
                    // granted, XP/level/stats, quest kill/item progress), replacing what the
                    // client used to have to separately pull via GetCharacterProgress/
                    // GetActiveQuestProgress after every win.
                    await NotifyCharacterUpdate(player, inventory: true, progress: true, questProgress: true, skillProgress: true);
                }
                else
                    logger.LogInformation("{Character} lost a solo fight", player.Name);
            }

            return new CombatTurnResult(
                true,
                newLogs,
                player.CurrentHealth,
                encounter.Enemy.CurrentHealth,
                encounter.Phase.ToString(),
                finished,
                won,
                xp,
                loot,
                CharacterMana: player.CurrentMana,
                CharacterMaxMana: player.MaxMana);
        }

        private static CombatTurnResult EmptyCombatTurn() =>
            new(false, [], 0, 0, "None", false, false, 0, []);

        private async Task HandlePartyLeave(string displayName, bool notifyCaller)
        {
            var currentPartyId = party.GetPartyId(displayName);
            if (currentPartyId is null) return;

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, PartyService.PartyGroup(currentPartyId));
            var (remaining, disbanded, newLeader, partyId) = party.Leave(displayName);
            logger.LogInformation("{User} left party {PartyId}{Disbanded}",
                displayName, partyId, disbanded ? " (party disbanded)" : "");

            if (disbanded)
            {
                if (remaining.Count == 1)
                {
                    var lastConn = presence.GetConnectionByName(remaining[0]);
                    if (lastConn is not null)
                    {
                        await Groups.RemoveFromGroupAsync(lastConn, PartyService.PartyGroup(partyId));
                        await Clients.Client(lastConn).SendAsync("PartyDisbanded");
                    }
                }
            }
            else
            {
                await Clients.Group(PartyService.PartyGroup(partyId))
                    .SendAsync("PartyUpdated", remaining.ToList(), newLeader ?? remaining[0]);
                await Clients.Group(PartyService.PartyGroup(partyId))
                    .SendAsync("ChatMessage", "System", $"{displayName} left the party.", "party");
            }

            if (notifyCaller)
                await Clients.Caller.SendAsync("PartyDisbanded");
        }

        // Returns true if targetCharacterName has blocked the sender's active character.
        private async Task<bool> IsBlockedByAsync(string senderAccount, string targetCharacterName)
        {
            var senderCharacterName = presence.GetCharacterNameByAccount(senderAccount);
            if (senderCharacterName is null) return false;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var targetChar = await db.Set<Myria.Server.Realm.Models.Character>()
                .FirstOrDefaultAsync(c => c.Name == targetCharacterName);
            if (targetChar is null) return false;

            var senderChar = await db.Set<Myria.Server.Realm.Models.Character>()
                .FirstOrDefaultAsync(c => c.Name == senderCharacterName);
            if (senderChar is null) return false;

            return await db.Set<Myria.Server.Realm.Models.Block>().AnyAsync(
                b => b.BlockerCharacterId == targetChar.Id && b.BlockedCharacterId == senderChar.Id);
        }

        private async Task RespawnCharacterAsync(Myria.Lib.Core.Entities.Characters.Character player, string connectionId)
        {
            player.ApplyDeathXpPenalty();

            int respawnRoomId = player.LastHealerRoomId ?? 1;
            var respawnRoom   = RoomService.AllRooms.FirstOrDefault(r => r.Id == respawnRoomId)
                                ?? RoomService.AllRooms.FirstOrDefault();
            if (respawnRoom is null) return;

            var displayName   = presence.GetDisplayName(connectionId);
            var currentRoomId = presence.GetRoom(connectionId);

            if (currentRoomId is int prev)
            {
                await Groups.RemoveFromGroupAsync(connectionId, RoomGroup(prev));
                if (displayName is not null)
                    await Clients.Group(RoomGroup(prev)).SendAsync("CharacterLeft", displayName);
            }

            player.Heal(int.MaxValue);
            player.RestoreMana(int.MaxValue);
            player.CurrentRoomId = respawnRoom.Id;
            player.CurrentRoom   = respawnRoom;
            presence.SetRoom(connectionId, respawnRoom.Id);

            await Groups.AddToGroupAsync(connectionId, RoomGroup(respawnRoom.Id));
            if (displayName is not null)
                await Clients.Group(RoomGroup(respawnRoom.Id)).SendAsync("CharacterEntered", displayName);

            // "CharacterRespawned" only ever carried the new room - the full heal above and
            // ApplyDeathXpPenalty()'s XP/level loss never reached the client at all before this.
            await NotifyCharacterUpdateFor(connectionId, player, vitals: true, progress: true);
            await Clients.Client(connectionId).SendAsync("CharacterRespawned", respawnRoom.Id, respawnRoom.Name);
        }

        private async Task RespawnDeadGroupMembersAsync(GroupCombatEncounter encounter)
        {
            foreach (var p in encounter.Characters.Where(p => !p.IsAlive))
            {
                var conn = presence.GetConnectionByName(p.Name);
                if (conn is not null)
                    await RespawnCharacterAsync(p, conn);
            }
        }

        // Persists every living combatant's post-fight state (XP/level, HP, etc.) right away.
        // Without this, a won encounter only ever lived in the in-memory session - the sole
        // other save paths are SaveSession (never actually called by the client) and the
        // best-effort save in OnDisconnectedAsync, which only runs once the connection times
        // out. A quick relaunch-and-relogin can beat that timeout, so LoadCharacter reads the
        // stale pre-fight DB row and the level-up silently reverts.
        private async Task SaveCombatParticipantsAsync(IEnumerable<Myria.Lib.Core.Entities.Characters.Character> characters)
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            foreach (var p in characters)
            {
                var conn = presence.GetConnectionByName(p.Name);
                var username = conn != null ? presence.GetAccountUsername(conn) : null;
                if (username == null) continue;
                // Retries and logs like every other save path - a silent catch hid lost combat progress (A10 F5).
                await SaveCharacterReliablyAsync(repo, username, p, "combat");
            }
        }

        // The three RookieService bonuses (XP/job-XP/purchase commission) are always applied
        // AFTER the primary action (combat win, craft, gather, upgrade, purchase) has already
        // succeeded and mutated in-memory state - an unguarded throw here used to propagate out
        // of the hub method entirely, aborting the response (and, in group combat, the save/
        // broadcast/respawn that follows) even though the primary action the player was told
        // about had already genuinely happened server-side. Swallowing failures here and simply
        // skipping the bonus keeps the primary action's outcome truthful (see TODO.md item 66).
        private async Task<long> TryApplyRookieXpBonusAsync(Myria.Lib.Core.Entities.Characters.Character player, long baseXp)
        {
            try
            {
                using var rookieScope = scopeFactory.CreateScope();
                return await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyXpBonusAsync(player, baseXp);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Rookie XP bonus failed for {Character} - continuing without it (see TODO.md item 66)",
                    player.Name);
                return 0;
            }
        }

        private async Task<long> TryApplyRookieJobXpBonusAsync(Myria.Lib.Core.Entities.Characters.Character player, string jobId, long baseJobXp)
        {
            try
            {
                using var rookieScope = scopeFactory.CreateScope();
                return await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyJobXpBonusAsync(player, jobId, baseJobXp);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Rookie job XP bonus failed for {Character}/{JobId} - continuing without it (see TODO.md item 66)",
                    player.Name, jobId);
                return 0;
            }
        }

        private async Task TryApplyPurchaseCommissionAsync(Myria.Lib.Core.Entities.Characters.Character player, long goldSpent)
        {
            try
            {
                using var rookieScope = scopeFactory.CreateScope();
                await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyPurchaseCommissionAsync(player, goldSpent);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Rookie purchase commission failed for {Character} - the purchase itself already succeeded (see TODO.md item 66)",
                    player.Name);
            }
        }

        private static string RoomGroup(int roomId) => $"room_{roomId}";

        /// <summary>Resolves a cast-skill request by id/name against the character's learned skills.</summary>
        /// <summary>Resolves a cast-skill request by id/name, with this player's own leveling/
        /// upgrades already applied (see SkillLevelingService.ResolveEffectiveSkill) - matches
        /// SkillSlotService.ResolveById's client-side resolution so multiplayer combat sees the
        /// same effective stats the client displays.</summary>
        private static Skill? ResolveCastableSkill(Myria.Lib.Core.Entities.Characters.Character player, string skillId)
        {
            var baseSkill = player.Skills.FirstOrDefault(s =>
                string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, skillId, StringComparison.OrdinalIgnoreCase));
            return baseSkill == null ? null : SkillLevelingService.ResolveEffectiveSkill(player, baseSkill);
        }

        /// <summary>Exact-id lookup of a consumable in the player's own session inventory (not
        /// InventoryUtils.ResolveInventoryItem's prefix match, which is meant for typed console
        /// input).</summary>
        private static ConsumableItem? ResolveConsumable(Myria.Lib.Core.Entities.Characters.Character player, string itemId) =>
            player.Inventory.Items.OfType<ConsumableItem>()
                .FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

        // Returns Clients.Caller for solo group fights; for real party fights, sends to exactly
        // the connections registered to this specific fight (see GroupCombatService) rather than
        // the party's persistent, room-independent PartyGroup - fightId is now scoped per room
        // (see StartGroupCombat), so a party with fights running in two different rooms at once
        // must not have both rooms' players receive each other's combat broadcasts.
        private IClientProxy CombatGroupClients(string fightId) =>
            fightId == Context.ConnectionId
                ? Clients.Caller
                : Clients.Clients(groupCombat.GetConnections(fightId));

        // Upper bound for one Craft call. Far above what the client offers ("max" = what the
        // ingredients allow), small enough that ingredient Amount * quantity can't overflow (A06 F3).
        public const int MaxCraftBatch = 10_000;

        /// <summary>v0.3: whether a monster type can currently be met in a room (its population hasn't run
        /// out). Without a loaded world state everything is available.</summary>
        private bool IsMonsterAvailable(int roomId, int monsterId) =>
            world.State?.IsMonsterAvailable(roomId, monsterId) ?? true;

        private bool CharacterRoomHasNpc(string npcId)
        {
            var roomId = presence.GetRoom(Context.ConnectionId);
            if (roomId is null) return false;
            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId.Value);
            return room is not null &&
                   room.Npcs.Any(n => string.Equals(n, npcId, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<int> GetCharacterIdByNameAsync(AppDbContext db, string charName) =>
            await db.Characters
                .Where(c => c.Name == charName)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();

        private static async Task<int?> GetGuildIdForCharacterAsync(AppDbContext db, int charId)
        {
            var fromMember = await db.GuildMembers
                .Where(m => m.CharacterId == charId)
                .Select(m => (int?)m.GuildId)
                .FirstOrDefaultAsync();
            if (fromMember.HasValue) return fromMember;
            return await db.GuildRookies
                .Where(r => r.CharacterId == charId)
                .Select(r => (int?)r.GuildId)
                .FirstOrDefaultAsync();
        }
    }
}
