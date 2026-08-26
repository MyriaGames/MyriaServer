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
        IServiceScopeFactory scopeFactory,
        ILogger<GameHub> logger
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

        public Task SetCharacterName(string characterName)
        {
            if (!string.IsNullOrWhiteSpace(characterName))
                presence.SetCharacterName(Context.ConnectionId, characterName.Trim());
            return NotifyShopReopenIfSealedAsync(characterName?.Trim());
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
            if (!owns) return false;

            // Prefer reattaching an already-live session for this character (see
            // CharacterSessionService.TryReattach) over a fresh DB read - a reload here would
            // silently discard any progress made in memory since the last save.
            var reattached = session.TryReattach(characterName, Context.ConnectionId);
            if (reattached is null)
            {
                var repo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
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
            bool questProgress = false, bool jobs = false, bool runes = false)
        {
            var dto = BuildCharacterUpdate(player, inventory, money, vitals, progress, questProgress, jobs, runes);
            return dto is null ? Task.CompletedTask : Clients.Caller.SendAsync("CharacterUpdated", dto);
        }

        /// <summary>Same as <see cref="NotifyCharacterUpdate"/>, but for a connection other than
        /// the caller (e.g. the other side of a completed trade).</summary>
        private Task NotifyCharacterUpdateFor(string connectionId, Myria.Lib.Core.Entities.Characters.Character player,
            bool inventory = false, bool money = false, bool vitals = false, bool progress = false,
            bool questProgress = false, bool jobs = false, bool runes = false)
        {
            var dto = BuildCharacterUpdate(player, inventory, money, vitals, progress, questProgress, jobs, runes);
            return dto is null ? Task.CompletedTask : Clients.Client(connectionId).SendAsync("CharacterUpdated", dto);
        }

        private static CharacterUpdateDto? BuildCharacterUpdate(
            Myria.Lib.Core.Entities.Characters.Character player,
            bool inventory, bool money, bool vitals, bool progress, bool questProgress = false,
            bool jobs = false, bool runes = false)
        {
            if (!inventory && !money && !vitals && !progress && !questProgress && !jobs && !runes) return null;

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
                runes ? player.KnownRunes.Select(r => new RuneSnapshot(r.Id, r.BaseRuneId, new List<string>(r.AddedWordIds))).ToList() : null);
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

        private static List<QuestProgressState> BuildQuestProgress(Myria.Lib.Core.Entities.Characters.Character player) =>
            player.ActiveQuests.Select(q => new QuestProgressState(
                q.Id,
                new Dictionary<int, int>(q.KillProgress),
                new Dictionary<string, int>(q.ItemProgress))).ToList();

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

            if (presence.GetRoom(Context.ConnectionId) != roomId)
                return new GatherActionResult(false, "wrong_room", null, 0, 0, null);

            if (session.GetEncounter(Context.ConnectionId) != null)
                return new GatherActionResult(false, "in_combat", null, 0, 0, null);

            var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == roomId);
            if (room == null || room.GatheringSpots.Count == 0)
                return new GatherActionResult(false, "no_spots", null, 0, 0, null);

            if (!session.TryConsumeGather(Context.ConnectionId, roomId, room.DailyGatherLimit + JobManager.GetGatherKnowledgeBonus(player), out int remaining))
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
                    using var rookieScope = scopeFactory.CreateScope();
                    await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                        .ApplyJobXpBonusAsync(player, jobId, xp);
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
            if (quantity <= 0)
                return new CraftActionResult(false, "invalid_quantity", null, 0, 0, null);

            var player = session.Get(Context.ConnectionId);
            if (player == null)
                return new CraftActionResult(false, "no_session", null, 0, 0, null);

            if (!CharacterRoomHasNpc(npcId))
                return new CraftActionResult(false, "npc_not_here", null, 0, 0, null);

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

            // Validate ingredients
            foreach (var ing in recipe.Ingredients)
            {
                int owned = player.Inventory.Items.Where(i => i.Id == ing.ItemId).Sum(i => i.StackSize);
                if (owned < ing.Amount * quantity)
                    return new CraftActionResult(false, "missing_ingredients", null, 0, 0, null);
            }

            // Consume ingredients
            foreach (var ing in recipe.Ingredients)
            {
                int remaining = ing.Amount * quantity;
                foreach (var stack in player.Inventory.Items.Where(i => i.Id == ing.ItemId).ToList())
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(stack.StackSize, remaining);
                    stack.StackSize -= take;
                    remaining -= take;
                    if (stack.StackSize <= 0) player.Inventory.RemoveItem(stack);
                }
            }

            if (!ItemFactory.TryCreateItem(recipeId, out var output))
            {
                // Refund on factory failure
                foreach (var ing in recipe.Ingredients)
                {
                    if (ItemFactory.TryCreateItem(ing.ItemId, out var refund))
                    {
                        refund.StackSize = ing.Amount * quantity;
                        player.Inventory.AddItem(refund, player);
                    }
                }
                return new CraftActionResult(false, "item_not_found", null, 0, 0, null);
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
                using var rookieScope = scopeFactory.CreateScope();
                await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyJobXpBonusAsync(player, jobId, xp);
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
                using var rookieScope = scopeFactory.CreateScope();
                await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyJobXpBonusAsync(player, jobId, xp);
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

            long totalCost = (long)item.BuyPrice * quantity;
            if (player.Money.Balance.BronzeTotal < totalCost)
                return new NpcShopBuyResult(false, "not_enough_money", totalCost, quantity);

            item.StackSize = quantity;
            if (!player.Inventory.AddItem(item, player))
                return new NpcShopBuyResult(false, "inventory_full", totalCost, quantity);

            player.Money.TrySpend(totalCost);

            // Credit guild treasury if buyer is a rookie
            using var rookieScope = scopeFactory.CreateScope();
            await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                .ApplyPurchaseCommissionAsync(player, totalCost);

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

            var invItem = player.Inventory.Items.FirstOrDefault(i => i.Id == itemId);
            if (invItem == null)
                return new NpcSellResult(false, "not_owned", 0, 0);

            if (invItem.StackSize < quantity)
                return new NpcSellResult(false, "not_enough_amount", 0, 0);

            long totalGain = (long)Myria.Lib.Core.Services.Manager.JobManager.GetSellValue(invItem, player) * quantity;

            if (invItem.StackSize == quantity)
                player.Inventory.RemoveItem(invItem);
            else
                invItem.StackSize -= quantity;

            player.Money.TryAdd(totalGain);

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
        /// Trusts the client's numbers as-is (matches EquipItem/UnequipItem - no server-side
        /// recompute), which is fine for a friends-only alpha with no anti-cheat concerns yet.
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
            int pointsSpent = strengthAdded + dexterityAdded + enduranceAdded + intelligenceAdded + spiritAdded;
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

            if (isReturn)
            {
                var active = player.ActiveQuests.FirstOrDefault(q => q.Id == quest.Id);
                if (active == null) return false;

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
                if (!player.ActiveQuests.Any(q => q.Id == quest.Id))
                {
                    var clone = quest.Clone();
                    clone.Status = clone.IsTalkOnly ? QuestStatus.Completed : QuestStatus.InProgress;
                    clone.GrantAcceptItems(player);
                    player.ActiveQuests.Add(clone);
                }
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

            bool ok = player.Inventory.SwapEquipment(itemId, player, out var reason);
            // Only the loose Items list (item removed from inventory into the equip slot) is
            // covered here - the Equipped/WeaponSlot assignment itself isn't part of
            // CharacterUpdateDto, so the client's own base.ExecuteEquip mirror stays the source
            // of truth for which slot actually holds what.
            if (ok) await NotifyCharacterUpdate(player, inventory: true);
            return new EquipItemResult(ok, reason);
        }

        public async Task<bool> UnequipItem(string slotType)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;

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
            await NotifyCharacterUpdate(player, inventory: true);
            return true;
        }

        public async Task<bool> UseItem(string itemId)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return false;
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

        public Task<bool> CombineSkills(List<string> skillIds)
        {
            var player = session.Get(Context.ConnectionId);
            if (player == null) return Task.FromResult(false);
            return Task.FromResult(SkillCombinationService.TryCreateForCharacter(player, skillIds) != null);
        }

        // ── Combat ────────────────────────────────────────────────────────────

        /// <summary>Abandons any active combat (solo or group) without awarding loot or XP.</summary>
        public Task AbandonCombat()
        {
            session.RemoveEncounter(Context.ConnectionId);
            groupCombat.RemoveConn(Context.ConnectionId);
            return Task.CompletedTask;
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

            if (session.GetEncounter(Context.ConnectionId) != null)
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

            var monster = MonsterService.PickMonsterForFight(monsterPool, room.EncounterableMonsters).Clone();
            var encounter = new CombatEncounter(player, monster);
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
            if (result.Finished && !result.CharacterWon)
            {
                if (player is not null)
                    await RespawnCharacterAsync(player, Context.ConnectionId);
            }
            else if (result.Finished && result.CharacterWon && result.XpGained > 0 && player is not null)
            {
                using var rookieScope = scopeFactory.CreateScope();
                long bonus = await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyXpBonusAsync(player, result.XpGained);
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
            if (result.Finished && !result.CharacterWon)
                await RespawnCharacterAsync(player, Context.ConnectionId);
            else if (result.Finished && result.CharacterWon && result.XpGained > 0)
            {
                using var rookieScope = scopeFactory.CreateScope();
                long bonus = await rookieScope.ServiceProvider.GetRequiredService<RookieService>()
                    .ApplyXpBonusAsync(player, result.XpGained);
                if (bonus > 0)
                    result = result with { RookieBonusXp = bonus };
            }
            return result;
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

            // Reject if already in any group combat.
            if (groupCombat.GetPartyId(Context.ConnectionId) is not null)
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
                // Party fight — gather all party members in this room.
                if (groupCombat.HasEncounter(partyId))
                    return new StartGroupCombatResult(false, "already_in_combat", [], [], "");

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
                    .Select(c => c!)
                    .ToList();

                fightId = partyId;
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

            var monsters = soloOnly
                ? MonsterService.PickMonstersForGroupFight(
                    room!.Monsters, room.EncounterableMonsters, room.CurrentMonsters, room.IsDungeonRoom, max: 1)
                : MonsterService.PickMonstersForGroupFight(
                    room!.Monsters, room.EncounterableMonsters, room.CurrentMonsters, room.IsDungeonRoom);
            var encounter = new GroupCombatEncounter(players, monsters);

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
            var displayName = DisplayName();
            var partyId     = groupCombat.GetPartyId(Context.ConnectionId);
            if (partyId is null)
                return EmptyGroupSnapshot();

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null) return EmptyGroupSnapshot();

            int logBefore = encounter.Log.Count;
            if (!encounter.CharacterAttack(displayName, targetMonsterIndex))
                return EmptyGroupSnapshot();

            var snap = BuildGroupSnapshot(encounter, logBefore);

            if (encounter.IsFinished)
            {
                if (encounter.CharactersWon)
                {
                    logger.LogInformation("Group combat won: {Chars}",
                        string.Join(", ", encounter.Characters.Select(c => $"{c.Name} (Lvl {c.Level})")));

                    using var rookieScope = scopeFactory.CreateScope();
                    var rookieSvc = rookieScope.ServiceProvider.GetRequiredService<RookieService>();
                    long totalMonsterExp = encounter.Monsters.Sum(m => m.Exp);
                    var bonusByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in encounter.Characters.Where(p => p.IsAlive))
                    {
                        long bonus = await rookieSvc.ApplyXpBonusAsync(p, totalMonsterExp);
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
                            await NotifyCharacterUpdateFor(connId, p, inventory: true, progress: true, questProgress: true);
                    }
                }

                groupCombat.RemoveByParty(partyId);
                await CombatGroupClients(partyId).SendAsync("GroupCombatFinished", snap);
                if (!encounter.CharactersWon)
                {
                    logger.LogInformation("Group combat lost: {Chars}",
                        string.Join(", ", encounter.Characters.Select(c => c.Name)));
                    await RespawnDeadGroupMembersAsync(encounter);
                }
            }
            else
            {
                await CombatGroupClients(partyId).SendAsync("GroupCombatUpdated", snap);
            }

            return snap;
        }

        public async Task<GroupCombatSnapshot> GroupCharacterCastSkill(string skillId, int targetIndex)
        {
            var displayName = DisplayName();
            var player      = session.Get(Context.ConnectionId);
            var partyId     = groupCombat.GetPartyId(Context.ConnectionId);
            if (player is null || partyId is null)
                return EmptyGroupSnapshot();

            var encounter = groupCombat.GetEncounter(partyId);
            if (encounter is null) return EmptyGroupSnapshot();

            var skill = ResolveCastableSkill(player, skillId);
            if (skill is null) return EmptyGroupSnapshot();

            int logBefore = encounter.Log.Count;
            if (!encounter.CharacterCastSkill(displayName, skill, targetIndex))
                return EmptyGroupSnapshot();

            var snap = BuildGroupSnapshot(encounter, logBefore);

            if (encounter.IsFinished)
            {
                if (encounter.CharactersWon)
                {
                    logger.LogInformation("Group combat won: {Chars}",
                        string.Join(", ", encounter.Characters.Select(c => $"{c.Name} (Lvl {c.Level})")));

                    using var rookieScope = scopeFactory.CreateScope();
                    var rookieSvc = rookieScope.ServiceProvider.GetRequiredService<RookieService>();
                    long totalMonsterExp = encounter.Monsters.Sum(m => m.Exp);
                    var bonusByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in encounter.Characters.Where(p => p.IsAlive))
                    {
                        long bonus = await rookieSvc.ApplyXpBonusAsync(p, totalMonsterExp);
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
                            await NotifyCharacterUpdateFor(connId, p, inventory: true, progress: true, questProgress: true);
                    }
                }

                groupCombat.RemoveByParty(partyId);
                await CombatGroupClients(partyId).SendAsync("GroupCombatFinished", snap);
                if (!encounter.CharactersWon)
                {
                    logger.LogInformation("Group combat lost: {Chars}",
                        string.Join(", ", encounter.Characters.Select(c => c.Name)));
                    await RespawnDeadGroupMembersAsync(encounter);
                }
            }
            else
            {
                await CombatGroupClients(partyId).SendAsync("GroupCombatUpdated", snap);
            }

            return snap;
        }

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

            await Clients.Client(targetConn).SendAsync("PartyInvite", displayName, partyId);
            await Clients.Caller.SendAsync("ChatMessage", "System",
                $"Party invite sent to {targetName}.", "party");
        }

        public async Task AcceptPartyInvite(string partyId)
        {
            var displayName = DisplayName();
            var (success, members, leader) = party.Join(partyId, displayName);

            if (!success)
            {
                await Clients.Caller.SendAsync("ChatMessage", "System",
                    "Could not join party — it may be full or no longer exists.", "party");
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

            var (tradeId, ok) = trade.Propose(Context.ConnectionId, displayName);
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
            trade.CancelPending(tradeId);
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

            // Execute the swap
            var snap = trade.Snapshot(Context.ConnectionId)!;
            var partnerConn = trade.PartnerConn(Context.ConnectionId)!;
            var partner = session.Get(partnerConn);
            if (partner is null) { await CancelTradeInternal("partner_offline"); return; }

            // Validate both sides still have the items and gold
            foreach (var offer in snap.TheirItems)
            {
                int owned = partner.Inventory.Items.Where(i => i.Id == offer.ItemId).Sum(i => i.StackSize);
                if (owned < offer.Quantity) { await CancelTradeInternal("item_not_available"); return; }
            }
            if (!partner.Money.CanAfford(snap.TheirGold))
            { await CancelTradeInternal("gold_not_available"); return; }

            foreach (var offer in snap.MyItems)
            {
                int owned = player.Inventory.Items.Where(i => i.Id == offer.ItemId).Sum(i => i.StackSize);
                if (owned < offer.Quantity) { await CancelTradeInternal("item_not_available"); return; }
            }
            if (!player.Money.CanAfford(snap.MyGold))
            { await CancelTradeInternal("gold_not_available"); return; }

            // Transfer items: mine → partner, theirs → me
            TransferItems(player, partner, snap.MyItems);
            TransferItems(partner, player, snap.TheirItems);

            // Transfer gold with J23 fame bonus applied to the seller (recipient)
            if (snap.MyGold > 0)
            {
                player.Money.TrySpend(snap.MyGold);
                long receipt = Myria.Lib.Core.Services.Manager.JobManager.GetCharacterSellReceipt(
                    partner, (int)Math.Min(snap.MyGold, int.MaxValue));
                partner.Money.TryAdd(receipt);
            }
            if (snap.TheirGold > 0)
            {
                partner.Money.TrySpend(snap.TheirGold);
                long receipt = Myria.Lib.Core.Services.Manager.JobManager.GetCharacterSellReceipt(
                    player, (int)Math.Min(snap.TheirGold, int.MaxValue));
                player.Money.TryAdd(receipt);
            }

            trade.End(Context.ConnectionId);
            logger.LogInformation("Trade completed between {A} and {B} ({AGold}/{BGold} gold, {AItems}/{BItems} item stacks)",
                player.Name, partner.Name, snap.MyGold, snap.TheirGold, snap.MyItems.Count, snap.TheirItems.Count);

            // Both sides' inventory/gold changed - each connection only gets its own update.
            await NotifyCharacterUpdate(player, inventory: true, money: true);
            await NotifyCharacterUpdateFor(partnerConn, partner, inventory: true, money: true);

            await Clients.Client(Context.ConnectionId).SendAsync("TradeCompleted");
            await Clients.Client(partnerConn).SendAsync("TradeCompleted");
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
            var existing = await shop.GetShopByOwnerNameAsync(displayName);
            if (existing is null) return;

            var player = session.Get(Context.ConnectionId);
            if (player is not null)
            {
                foreach (var stored in existing.Items)
                {
                    if (!Myria.Lib.Core.Services.Builder.ItemFactory.TryCreateItem(stored.ItemId, out var restored) || restored is null)
                        continue;
                    restored.StackSize = stored.Quantity;
                    player.Inventory.AddItem(restored, player);
                }
            }

            var roomId = await shop.CloseShopAsync(displayName);
            if (roomId is int rid)
                await Clients.Group(RoomGroup(rid)).SendAsync("ShopClosed", displayName);
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

            var displayName = DisplayName();
            var beforeShop   = await shop.GetShopByOwnerNameAsync(displayName);
            if (beforeShop is null) return 0;

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

            var (item, state) = await shop.ValidatePurchaseAsync(ownerName, itemId, qty);
            if (item is null)
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false,
                    state.Visible ? "no_listing" : "shop_closed", itemId, 0, 0L);
                return;
            }

            long totalCost = item.Price!.Value * qty;
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

            if (!await shop.CommitSaleAsync(ownerName, itemId, qty))
            {
                await Clients.Caller.SendAsync("ShopBuyResult", false, "insufficient_stock", itemId, 0, 0L);
                return;
            }

            newItem.StackSize = qty;
            buyer.Inventory.AddItem(newItem, buyer);
            buyer.Money.TrySpend(totalCost);

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

        private string DisplayName() =>
            presence.GetDisplayName(Context.ConnectionId) ?? Context.User!.Identity!.Name!;

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
                    await NotifyCharacterUpdate(player, inventory: true, progress: true, questProgress: true);
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
                try { await repo.SaveAsync(username, p); } catch { /* best-effort */ }
            }
        }

        private static string RoomGroup(int roomId) => $"room_{roomId}";

        /// <summary>
        /// Resolves a cast-skill request by id/name against everything the character can
        /// actually cast in combat - regular learned skills, plus combined and composite-fusion
        /// skills' resolved forms. Mirrors SkillSlotService.ResolveById/GetCombatSkills, which is
        /// what the client's skill bar is built from and what its Id/Name it sends here comes
        /// from; a plain player.Skills-only lookup here silently failed to find combined/fusion
        /// skills (never added to Skills, only to CombinedSkills/CompositeSkills), making them
        /// uncastable in multiplayer even though the client fully supports slotting them.
        /// </summary>
        private static Skill? ResolveCastableSkill(Myria.Lib.Core.Entities.Characters.Character player, string skillId)
        {
            var skill = player.Skills.FirstOrDefault(s =>
                string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, skillId, StringComparison.OrdinalIgnoreCase));
            if (skill != null) return skill;

            skill = player.CombinedSkills
                .Select(c => c.ResolvedSkill)
                .FirstOrDefault(s => s != null &&
                    (string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Name, skillId, StringComparison.OrdinalIgnoreCase)));
            if (skill != null) return skill;

            return player.CompositeSkills
                .Select(c => c.ResolvedSkill)
                .FirstOrDefault(s => s != null &&
                    (string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Name, skillId, StringComparison.OrdinalIgnoreCase)));
        }

        // Returns Clients.Caller for solo group fights, party group for real parties.
        private IClientProxy CombatGroupClients(string fightId) =>
            fightId == Context.ConnectionId
                ? Clients.Caller
                : Clients.Group(PartyService.PartyGroup(fightId));

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
