# Myria.Server.Realm

**Myria.Server.Realm** (published binary name: `MyriaServer`) is the authoritative realtime
game/world server for **Myria** — an ASP.NET Core 8 + SignalR service that holds all
character/account game state, runs the live gameplay loop (combat, movement, chat, trading,
guilds, parties, shops), and exposes a REST API for character management and the game's social
systems. One running instance of this service is a **realm**: a self-contained game world with
its own player database. Multiple realms can run side by side, each validating JWTs issued by
[MyriaAuthServer](https://github.com/MyriaGames/MyriaAuthServer), which is also where the list of
available realms is configured.

## Feature surface

Most gameplay actions are invoked over the SignalR hub at `/hubs/game`
(`Hubs/GameHub.cs`), which the client connects to after authenticating. A smaller set of
account-scoped operations (character CRUD, guild/friend/block management outside of live
gameplay) go through the REST controllers instead.

**Realtime (SignalR hub — `/hubs/game`):**
- **Session & presence** — load/save a character into a live server-side session
  (`LoadCharacter`, `SaveSession`), room join/leave with per-room presence broadcasts (`JoinRoom`)
- **Chat** — global, room, party, guild, and whisper channels, with block-list enforcement on
  whispers (`SendMessage`)
- **Combat** — solo turn-based combat against room/dungeon monsters (`StartCombat`,
  `CharacterAttack`, `CharacterCastSkill`), plus authoritative XP/level/quest-progress sync
- **Group combat** — party-based multi-character encounters with turn order and live snapshots
  (`StartGroupCombat`, `GroupCharacterAttack`, `GroupCharacterCastSkill`, `GetGroupCombatState`)
- **Parties** — invite/accept/decline/leave/kick/transfer-leadership (`InviteToParty`,
  `AcceptPartyInvite`, `KickFromParty`, `TransferPartyLeader`, ...)
- **Gathering, crafting & upgrading** — job-gated resource gathering, NPC crafting recipes, and
  equipment upgrading (`Gather`, `Craft`, `Upgrade`)
- **Jobs & skills** — active job switching, skill slotting/combining (`ToggleJob`, `SlotSkill`,
  `CombineSkills`)
- **Inventory & equipment** — equip/unequip, use consumables, stat point allocation
  (`EquipItem`, `UnequipItem`, `UseItem`, `SyncStatAllocation`)
- **Quests & runes** — accept/turn in quests, grant runes (`QuestAction`, `GrantRune`)
- **NPC shops** — buy from and sell to NPC vendors (`BuyFromNpcShop`, `SellItemToNpc`)
- **Player-to-player trading** — propose/accept/cancel a live trade session with item and gold
  offers (`ProposeTrade`, `AddTradeItem`, `SetTradeGold`, `ConfirmTrade`)
- **Player shops** — open/close a personal shop, deposit/withdraw/list items, browse and buy from
  another player's shop (`OpenShop`, `DepositShopItem`, `BrowseShop`, `BuyFromShop`)
- **Guild live events** — invites, promotions/demotions, rookie hiring, disbanding
  (`GuildSendInvite`, `GuildPromote`, `GuildHireRookie`, `GuildDisband`)

**REST API (Controllers/):**
- `CharactersController` — authenticated CRUD for the caller's own characters
  (`GET/POST /api/characters`, `GET/DELETE /api/characters/{name}`)
- `GuildsController` — guild lifecycle, invites, applications, membership/rank management, and
  guild property (houses and bases with buildable rooms) under `/api/guilds/...`
- `FriendsController` — friend requests and friend list with live online/party status
  (`/api/friends`)
- `BlocksController` — block/unblock other characters (`/api/blocks`)
- `PublicCharactersController` — unauthenticated, read-only character registry and equipped-gear
  detail (`/api/public/characters`)
- `AdminController` — internal, service-to-service only endpoint used by MyriaAuthServer for
  GDPR account-deletion and rename cascades (`/api/admin/characters/...`), authenticated via a
  shared `X-Internal-Secret` header rather than a player JWT
- `StatusController` — simple online player count (`/api/status`)

A character's live in-memory session is only ever reattached to a new connection after the
server verifies, against the database, that the authenticated account actually owns that
character — matching purely by character name would otherwise let any logged-in player hijack
another player's live session by name alone.

Character names are enforced globally unique (case-insensitive) across every account on a realm,
so a whisper, trade, friend request, or guild invite by name always resolves to the intended
player.

## Architecture

Myria is split across several repositories under the [MyriaGames](https://github.com/MyriaGames)
GitHub org:

- **[MyriaAuthServer](https://github.com/MyriaGames/MyriaAuthServer)** — the shared account/auth
  service. It handles registration, login, and password management, and mints the JWTs that this
  realm validates. It also owns the list of available realms and reaches this service's internal
  admin API for account-deletion/rename cascades.
- **Myria.Server.Realm** (this repo) — the authoritative game/world server described above. It
  trusts tokens minted by MyriaAuthServer purely via a shared signing key, with no live callback
  to that service for ordinary gameplay.
- **[Myria.Lib](https://github.com/MyriaGames/MyriaLib)** — shared game-logic library (entities,
  items, skills, combat/quest/job systems) referenced by this server and by the game clients, so
  gameplay math and content stay identical across client and server.
- **Game clients** that connect to a realm over its REST API and SignalR hub:
  [MyriaRPG](https://github.com/MyriaGames/MyriaRPG) (WPF),
  [ConsoleWorldRPG](https://github.com/MyriaGames/ConsoleWorldRPG) (console), and
  [MyriaWorld](https://github.com/MyriaGames/MyriaWorld) (MonoGame, in development).

## Requirements

- .NET 8 SDK (`net8.0` target framework)

## Getting started / local development

```
dotnet run --project Myria.Server.Realm.csproj
```

By default (the `http`/`https` launch profiles in `Properties/launchSettings.json`, which force
`ASPNETCORE_ENVIRONMENT=Development`) the server listens on `http://localhost:5001` (or
`https://localhost:5001` with the `https` profile), and opens the Swagger UI at `/swagger`.
Local data is stored in a SQLite file at `Storage/myria.db`, created automatically on first run
(EF Core migrations are applied at startup).

To actually authenticate players locally, you need a matching
[MyriaAuthServer](https://github.com/MyriaGames/MyriaAuthServer) instance running alongside this
server, with an **identical `Jwt` configuration block** (`Jwt:Key`, `Jwt:Issuer`,
`Jwt:Audience`). This realm validates tokens purely by verifying the shared signing key locally —
there is no live call back to the auth server — so any mismatch between the two services' `Jwt`
settings will make every token this realm issues (or receives) fail validation.

The committed `appsettings.json` ships a placeholder `Jwt:Key`
(`REPLACE_ME_DEV_PLACEHOLDER_KEY_AT_LEAST_32_BYTES_LONG`) that's fine for local development as
long as MyriaAuthServer's own dev config uses the same value.

## Configuration for Production deployment

`Program.cs` actively refuses to start when `ASPNETCORE_ENVIRONMENT=Production` unless all of the
following are satisfied. None of these should ever be committed to `appsettings.json` (which
ships inside the public repo and the published release zip) — supply them via environment
variables (ASP.NET Core's double-underscore binding convention) or a local, gitignored
`appsettings.Production.json`.

| Setting | How to set it | Requirement |
|---|---|---|
| JWT signing key | `Jwt__Key` env var (or `Jwt:Key` in `appsettings.Production.json`) | Must not be empty or the dev placeholder; **must be byte-identical to MyriaAuthServer's `Jwt:Key`** |
| Internal admin secret | `Admin__InternalSecret` env var (or `Admin:InternalSecret`) | Must not be empty or the dev placeholder; **must be byte-identical to MyriaAuthServer's `Admin:InternalSecret`** — sent as the `X-Internal-Secret` header when MyriaAuthServer calls this realm's `/api/admin/characters/...` endpoints |
| HTTPS endpoint | `Kestrel:Endpoints:Https:Url`, `Kestrel:Endpoints:Https:Certificate:Path`, `Kestrel:Endpoints:Https:Certificate:Password` | All three required; the certificate file at `Certificate:Path` must exist on disk. Production serves no plain-HTTP endpoint — Bearer tokens and character data are never sent unencrypted |
| Database location | `ConnectionStrings__DefaultConnection` (or `ConnectionStrings:DefaultConnection`), e.g. `Data Source=Storage/myria.db` | Each realm instance should use its own SQLite file; a relative `Data Source` is resolved against the app's base directory, not the process's working directory |

Also relevant to a multi-realm/production setup:
- `Jwt:Issuer` and `Jwt:Audience` must also match MyriaAuthServer's values (they're part of the
  same shared `Jwt` block, alongside `Jwt:Key`).
- Each realm instance typically needs its own `Kestrel` URL/port and its own database file — this
  is normally handled with a per-realm `appsettings.{RealmId}.json` or environment variables at
  launch. The directory of which realms exist (their ids/names/URLs) is configured on
  MyriaAuthServer, not here.
- `run-production.ps1` / `run-production.sh` start the server with
  `ASPNETCORE_ENVIRONMENT=Production` set (the `dotnet run` launch profiles always force
  `Development`, so they're not suitable for real hosting). `update-production.sh` updates an
  existing production deployment to a newer published release without touching `Storage/`,
  `appsettings.Production.json`, or `certs/`.

## Legal / Privacy

This service stores gameplay and character data tied to player accounts. See the `Legal/` folder
for the project's legal documents (in German, per Austrian/GDPR requirements):

- [`Legal/Impressum.md`](Legal/Impressum.md) — legal notice / site operator disclosure.
- [`Legal/Datenschutzerklaerung.md`](Legal/Datenschutzerklaerung.md) — privacy policy.
- [`Legal/Nutzungsbedingungen.md`](Legal/Nutzungsbedingungen.md) — terms of use.

## License

MIT — see [LICENSE](LICENSE).

## Status

Active alpha. This is a hobby project under ongoing development; expect breaking changes.
