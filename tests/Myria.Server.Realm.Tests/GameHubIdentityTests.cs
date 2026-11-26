using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Repositories;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Hubs;
using Myria.Server.Realm.Repositories;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// "Who is calling" on the game hub. The presence name a connection carries is what ~45 hub
/// methods (guild, shop, trade, party, chat) act on and what whispers/invites are routed to, so it
/// must only ever be the character that connection's account owns and has loaded.
/// Security review 2026-11-25, A01 F1: SetCharacterName used to accept any name, letting any player
/// act as any other character (e.g. withdraw from their shop, disband their guild).
/// </summary>
[Collection("RealmDb")]
public class GameHubIdentityTests
{
    public GameHubIdentityTests(RealmDbFixture _) { }

    private const string ItemId = "starter_dagger";

    [Fact]
    public async Task LoadCharacter_OwnCharacter_SetsDisplayNameToIt()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");

        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));

        Assert.Equal("Bobchar", realm.Presence.GetDisplayName(bob.ConnectionId));
    }

    [Fact]
    public async Task LoadCharacter_ForeignCharacter_IsRefused_AndNameUnchanged()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");

        Assert.False(await realm.Hub(bob).LoadCharacter("Alicechar"));

        Assert.Equal("bob", realm.Presence.GetDisplayName(bob.ConnectionId)); // still the account
    }

    [Fact]
    public async Task SetCharacterName_ForeignCharacter_IsIgnored()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");

        await realm.Hub(bob).SetCharacterName("Alicechar");

        Assert.Equal("Bobchar", realm.Presence.GetDisplayName(bob.ConnectionId));
        Assert.Null(realm.Presence.GetConnectionByName("Alicechar")); // whispers/invites can't be hijacked
    }

    [Fact]
    public async Task SetCharacterName_WithoutLoadedCharacter_IsIgnored()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");

        await realm.Hub(bob).SetCharacterName("Alicechar");

        Assert.Equal("bob", realm.Presence.GetDisplayName(bob.ConnectionId));
    }

    [Fact]
    public async Task SetCharacterName_OwnLoadedCharacter_StillWorks_ForExistingClients()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");

        await realm.Hub(bob).SetCharacterName("bobchar"); // clients may differ in case

        Assert.Equal("Bobchar", realm.Presence.GetDisplayName(bob.ConnectionId));
    }

    [Fact]
    public async Task WithdrawShopItem_AfterSpoofAttempt_CannotTakeFromAnotherPlayersShop()
    {
        using var realm = await HubHarness.CreateAsync();
        using (var setup = realm.NewShopService())
        {
            Assert.True((await setup.Service.OpenShopAsync("Alicechar", roomId: 1)).ok);
            Assert.True((await setup.Service.DepositAsync("Alicechar", ItemId, 3)).ok);
        }
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");
        await realm.Hub(bob).SetCharacterName("Alicechar");

        var withdrawn = await realm.Hub(bob).WithdrawShopItem(ItemId, 3);

        Assert.Equal(0, withdrawn);
        using var check = realm.NewShopService();
        var aliceShop = await check.Service.GetShopByOwnerNameAsync("Alicechar");
        Assert.Equal(3, aliceShop!.Items.Single(i => i.ItemId == ItemId).Quantity);
    }
}

/// <summary>
/// Runs GameHub methods directly against a private realm database with the real services, a fake
/// SignalR connection context and no-op client proxies. Two accounts: alice owns "Alicechar",
/// bob owns "Bobchar".
/// </summary>
internal sealed class HubHarness : IDisposable
{
    private readonly RealmDbHost _db = new();
    private readonly ServiceProvider _services;

    public CharacterPresenceService Presence { get; } = new();
    public CharacterSessionService Session { get; } = new();
    public PartyService Party { get; } = new();
    public TradeService Trade { get; } = new();
    public GroupCombatService GroupCombat { get; } = new();
    public PvpCombatService PvpCombat { get; } = new();
    /// <summary>v0.3 world state (NPC stock, room populations), loaded from the test database in CreateAsync.</summary>
    public WorldStateService World { get; private set; } = null!;
    /// <summary>v0.3 dynamic quests (grants), loaded from the test database in CreateAsync.</summary>
    public DynamicQuestService Quests { get; private set; } = null!;
    /// <summary>v0.3 per-party dungeon runs.</summary>
    public DungeonInstanceService Dungeons { get; } = new();

    /// <summary>The logger hubs get; replace to capture log output.</summary>
    public ILogger<GameHub> Logger { get; set; } = NullLogger<GameHub>.Instance;
    public CharacterLocks Locks { get; } = new();
    /// <summary>v0.3: the client-version gate (OnConnectedAsync) - not exercised by the harness, which registers
    /// connections directly.</summary>
    public Myria.Server.Realm.Models.AllowedVersionsConfig AllowedVersions { get; } = new();

    /// <summary>A fresh context over the test database, to read back what was actually saved.</summary>
    public Myria.Server.Realm.Data.AppDbContext NewDb() => _db.NewContext();
    public IServiceScopeFactory ScopeFactory => _services.GetRequiredService<IServiceScopeFactory>();

    private HubHarness()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped<ICharacterRepository, SqlCharacterRepository>();
        services.AddScoped<BanService>();
        services.AddScoped<GuildService>();
        services.AddScoped<PlayerShopService>();
        services.AddSingleton(Presence);
        services.AddSingleton(Session);
        services.AddSingleton(Party);
        services.AddSingleton(Trade);
        services.AddSingleton(GroupCombat);
        services.AddSingleton(new Models.GuildConfig());
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public static async Task<HubHarness> CreateAsync()
    {
        var harness = new HubHarness();
        harness.World = new WorldStateService(harness._services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldStateService>.Instance);
        await harness.World.LoadAsync();
        harness.Quests = new DynamicQuestService(harness._services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DynamicQuestService>.Instance);
        await harness.Quests.LoadAsync();
        var repo = new SqlCharacterRepository(harness._db.Db);
        await repo.SaveAsync("alice", new Character("Alicechar", new Stats()));
        await repo.SaveAsync("bob", new Character("Bobchar", new Stats()));
        return harness;
    }

    /// <summary>Simulates OnConnectedAsync's registration for an authenticated account.</summary>
    public FakeCallerContext Connect(string account)
    {
        var ctx = new FakeCallerContext(account);
        Presence.Connect(ctx.ConnectionId, account);
        return ctx;
    }

    public GameHub Hub(FakeCallerContext ctx)
    {
        var scope = _services.CreateScope();
        return new GameHub(
            Presence, Party, Session, Trade,
            scope.ServiceProvider.GetRequiredService<PlayerShopService>(),
            GroupCombat,
            PvpCombat,
            World,
            Quests,
            Dungeons,
            _services.GetRequiredService<IServiceScopeFactory>(),
            AllowedVersions,
            Logger,
            Locks)
        {
            Context = ctx,
            Clients = new NoopClients(),
            Groups = new NoopGroups()
        };
    }

    public ShopLease NewShopService() => new(_services.CreateScope());

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    internal sealed class ShopLease(IServiceScope scope) : IDisposable
    {
        public PlayerShopService Service { get; } = scope.ServiceProvider.GetRequiredService<PlayerShopService>();
        public void Dispose() => scope.Dispose();
    }
}

internal sealed class FakeCallerContext(string account) : HubCallerContext
{
    public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public override string? UserIdentifier => account;
    public override ClaimsPrincipal? User { get; } =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], "test"));
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() { }
}

internal sealed class NoopProxy : ISingleClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default) =>
        Task.FromResult(default(T)!);
}

internal sealed class NoopClients : IHubCallerClients
{
    private static readonly NoopProxy Proxy = new();
    public ISingleClientProxy Caller => Proxy;
    IClientProxy IHubCallerClients<IClientProxy>.Caller => Proxy;
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Proxy;
    public IClientProxy Others => Proxy;
    public IClientProxy All => Proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public ISingleClientProxy Client(string connectionId) => Proxy;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
    public IClientProxy Group(string groupName) => Proxy;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
    public IClientProxy OthersInGroup(string groupName) => Proxy;
    public IClientProxy User(string userId) => Proxy;
    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

internal sealed class NoopGroups : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
