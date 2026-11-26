using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Myria.Server.Realm.Controllers;
using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>
/// Security review 2026-11-25, A07 F1: after a password change or reset the auth server revokes the
/// account's sessions on every realm; tokens issued before that must stop working here.
/// </summary>
public class SessionRevocationTests : IDisposable
{
    private const string Secret = "test-internal-secret";
    private readonly RealmDbHost _db = new();
    private readonly ServiceProvider _services;
    private readonly IConfiguration _config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:ExpirationHours"] = "24",
            ["Admin:InternalSecret"] = Secret,
        })
        .Build();

    public SessionRevocationTests()
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

    private SessionRevocationService NewService() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _config);

    private static readonly DateTime T0 = DateTime.UtcNow.AddMinutes(-10);

    [Fact]
    public void WithoutRevocation_EveryValidTokenIsAccepted_EvenWithoutIssuedAt()
    {
        var svc = NewService();
        Assert.True(svc.IsTokenAccepted("alice", T0));
        Assert.True(svc.IsTokenAccepted("alice", null));
    }

    [Fact]
    public async Task TokenIssuedBeforeRevocation_IsRejected_TokenFromThatSecondOnIsAccepted()
    {
        var svc = NewService();
        await svc.RevokeAsync("alice", T0);

        Assert.False(svc.IsTokenAccepted("alice", T0.AddSeconds(-1)));
        Assert.True(svc.IsTokenAccepted("alice", T0));            // the fresh token from the same request
        Assert.True(svc.IsTokenAccepted("alice", T0.AddMinutes(1)));
        Assert.True(svc.IsTokenAccepted("bob", T0.AddHours(-1)));  // other accounts untouched
    }

    [Fact]
    public async Task TokenWithoutIssuedAt_IsRejected_OnceTheAccountHasARevocation()
    {
        var svc = NewService();
        await svc.RevokeAsync("alice", T0);
        Assert.False(svc.IsTokenAccepted("alice", null));
    }

    [Fact]
    public async Task Revocation_IsCaseInsensitive_LikeUsernames()
    {
        var svc = NewService();
        await svc.RevokeAsync("Alice", T0);
        Assert.False(svc.IsTokenAccepted("alice", T0.AddSeconds(-5)));
    }

    [Fact]
    public async Task Revocation_SurvivesARealmRestart()
    {
        await NewService().RevokeAsync("alice", T0);

        var restarted = NewService();
        await restarted.LoadAsync();

        Assert.False(restarted.IsTokenAccepted("alice", T0.AddSeconds(-1)));
    }

    [Fact]
    public async Task OlderRevocation_NeverMovesTheCutOffBack()
    {
        var svc = NewService();
        await svc.RevokeAsync("alice", T0);
        await svc.RevokeAsync("alice", T0.AddMinutes(-5));

        Assert.False(svc.IsTokenAccepted("alice", T0.AddSeconds(-1)));
        var restarted = NewService();
        await restarted.LoadAsync();
        Assert.False(restarted.IsTokenAccepted("alice", T0.AddSeconds(-1)));
    }

    [Fact]
    public async Task RevocationOlderThanTheTokenLifetime_IsPurged()
    {
        await NewService().RevokeAsync("alice", DateTime.UtcNow.AddHours(-30));

        var restarted = NewService();
        await restarted.LoadAsync();

        Assert.True(restarted.IsTokenAccepted("alice", null));
        Assert.Empty(_db.NewContext().SessionRevocations);
    }

    // ── The operator endpoint ──────────────────────────────────────────────

    private AdminController NewController(SessionRevocationService svc, string remoteIp, string? secret)
    {
        var db = _db.NewContext();
        var controller = new AdminController(db, _config, new BanService(db), new CharacterPresenceService(),
            svc, hub: null!, // hub only used to kick a live connection; none exists here
            new CharacterSessionService(), new Myria.Server.Realm.Repositories.SqlCharacterRepository(db), new CharacterLocks());
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        http.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.2");
        if (secret is not null) http.Request.Headers["X-Internal-Secret"] = secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static RevokeSessionsRequest At(DateTime t) => new() { TokensValidAfter = new DateTimeOffset(t).ToUnixTimeSeconds() };

    [Fact]
    public async Task RevokeEndpoint_FromThisMachineWithSecret_Revokes()
    {
        var svc = NewService();
        var result = await NewController(svc, "127.0.0.1", Secret).RevokeSessions("alice", At(T0));

        Assert.IsType<OkObjectResult>(result);
        Assert.False(svc.IsTokenAccepted("alice", T0.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("203.0.113.9", Secret)]  // internet, even with the secret
    [InlineData("127.0.0.1", "wrong")]
    [InlineData("127.0.0.1", null)]
    public async Task RevokeEndpoint_WithoutBothGates_IsForbidden(string remoteIp, string? secret)
    {
        var svc = NewService();
        var result = await NewController(svc, remoteIp, secret).RevokeSessions("alice", At(T0));

        Assert.IsType<ForbidResult>(result);
        Assert.True(svc.IsTokenAccepted("alice", T0.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public async Task RevokeEndpoint_RejectsNonsenseTimes(long unixSeconds)
    {
        var result = await NewController(NewService(), "127.0.0.1", Secret)
            .RevokeSessions("alice", new RevokeSessionsRequest { TokensValidAfter = unixSeconds });
        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task RevokeEndpoint_RejectsAFarFutureCutOff_ThatWouldLockTheAccountOut()
    {
        var svc = NewService();
        var result = await NewController(svc, "127.0.0.1", Secret).RevokeSessions("alice", At(DateTime.UtcNow.AddHours(2)));

        Assert.IsType<BadRequestResult>(result);
        Assert.True(svc.IsTokenAccepted("alice", DateTime.UtcNow));
    }
}
