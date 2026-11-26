using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Myria.Server.Realm.Controllers;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Models.Dto;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// The account lookup the admin site shows next to a password-reset request (security review
/// 2026-11-25, A07 F3): characters with level/class and their guild.
/// </summary>
public class AdminAccountLookupTests : IDisposable
{
    private const string Secret = "test-internal-secret";
    private readonly RealmDbHost _db = new();
    private readonly ServiceProvider _services;
    private readonly IConfiguration _config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:InternalSecret"] = Secret })
        .Build();

    public AdminAccountLookupTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private AdminController NewController()
    {
        var db = _db.NewContext();
        var controller = new AdminController(db, _config, new BanService(db), new CharacterPresenceService(),
            new SessionRevocationService(_services.GetRequiredService<IServiceScopeFactory>(), _config), hub: null!,
            new CharacterSessionService(), new Myria.Server.Realm.Repositories.SqlCharacterRepository(db), new CharacterLocks());
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers["X-Internal-Secret"] = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task GetAccount_ListsCharactersWithTheirGuildAndRank()
    {
        var leader = new Character { UserId = "alice", Name = "Alicia", Level = 12 };
        var loner = new Character { UserId = "alice", Name = "Brann", Level = 3 };
        _db.Db.Characters.AddRange(leader, loner);
        await _db.Db.SaveChangesAsync();
        var guild = new Guild { Name = "Silver Hand", Tag = "SH", LeaderCharacterId = leader.Id };
        _db.Db.Guilds.Add(guild);
        await _db.Db.SaveChangesAsync();
        _db.Db.GuildMembers.Add(new GuildMember { GuildId = guild.Id, CharacterId = leader.Id, Rank = GuildRank.Leader });
        await _db.Db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await NewController().GetAccount("alice"));
        var account = Assert.IsType<RealmAccountInfo>(ok.Value);

        Assert.Equal("Silver Hand (Leader)", account.Characters.Single(c => c.Name == "Alicia").Guild);
        Assert.Null(account.Characters.Single(c => c.Name == "Brann").Guild);
        Assert.Equal(12, account.Characters.Single(c => c.Name == "Alicia").Level);
    }
}
