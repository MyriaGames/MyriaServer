using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Myria.Server.ClassicRealm.Controllers;
using Myria.Server.ClassicRealm.Hubs;
using Myria.Server.ClassicRealm.Repositories;
using Myria.Server.ClassicRealm.Services;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>Security review 2026-11-26, A10 F4: an account rename must not lose a live session's progress.</summary>
[Collection("RealmDb")]
public class RenameDuringPlayTests
{
    private const string Secret = "test-internal-secret";

    private sealed class NoopHubClients : IHubClients
    {
        private static readonly NoopProxy P = new();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public ISingleClientProxy Client(string connectionId) => P;
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }

    private sealed class NoopHubContext : IHubContext<GameHub>
    {
        public IHubClients Clients { get; } = new NoopHubClients();
        public IGroupManager Groups { get; } = new NoopGroups();
    }

    [Fact]
    public async Task Rename_SavesTheLiveCharacterFirst_ThenMovesIt()
    {
        using var realm = await HubHarness.CreateAsync();
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");
        realm.Session.Get(bob.ConnectionId)!.Money.TryAdd(777);   // in memory only, not saved yet

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:InternalSecret"] = Secret, ["Jwt:ExpirationHours"] = "24" })
            .Build();
        using var db = realm.NewDb();
        var controller = new AdminController(db, config, new BanService(db), realm.Presence,
            new SessionRevocationService(null!, config), new NoopHubContext(),
            realm.Session, new SqlCharacterRepository(db), realm.Locks);
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers["X-Internal-Secret"] = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await controller.RenameUser(new RenameUserRequest { OldUsername = "bob", NewUsername = "robert" });

        Assert.IsType<NoContentResult>(result);
        Assert.Null(realm.Session.Get(bob.ConnectionId));   // session ended - nothing left to save under "bob"
        using var check = realm.NewDb();
        var row = check.Characters.Single(c => c.Name == "Bobchar");
        Assert.Equal("robert", row.UserId);
        Assert.Equal(777, row.MoneyBronze);                 // the unsaved progress made it
    }
}
