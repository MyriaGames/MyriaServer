using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Myria.Server.ClassicRealm.Models;
using Myria.Server.ClassicRealm.Controllers;
using Myria.Server.ClassicRealm.Hubs;
using Myria.Server.ClassicRealm.Services;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>Collects formatted log lines for assertions.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Lines { get; }
    public CapturingLogger() : this(new()) { }
    internal CapturingLogger(List<(LogLevel, string)> sink) => Lines = sink;
    public IEnumerable<string> Security => Lines.Select(l => l.Message).Where(m => m.StartsWith("security "));
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public List<(LogLevel Level, string Message)> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CapturingLogger<object>(Lines);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

/// <summary>
/// Security review 2026-11-26, A09 F2: refused access on the realm leaves a trace - another player's
/// character, a refused operator call, a rejected token - without tokens, secrets or IP addresses.
/// </summary>
[Collection("RealmDb")]
public class RealmSecurityLogTests : IDisposable
{
    private const string Secret = "test-internal-secret";
    private readonly RealmDbHost _db = new();
    private readonly ServiceProvider _services;
    private readonly IConfiguration _config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:InternalSecret"] = Secret })
        .Build();

    public RealmSecurityLogTests(RealmDbFixture _)
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

    // ── Hub: LoadCharacter ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadingAnotherPlayersCharacter_IsAWarning()
    {
        using var realm = await HubHarness.CreateAsync();
        var log = new CapturingLogger<GameHub>();
        realm.Logger = log;
        var bob = realm.Connect("bob");

        Assert.False(await realm.Hub(bob).LoadCharacter("Alicechar"));

        var line = Assert.Single(log.Lines, l => l.Message.StartsWith("security "));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("security realm.character.not_owner user=bob outcome=refused detail=hub:LoadCharacter;Alicechar",
            line.Message);
    }

    [Fact]
    public async Task LoadingACharacterThatDoesNotExist_IsNotASecurityEvent()
    {
        using var realm = await HubHarness.CreateAsync();
        var log = new CapturingLogger<GameHub>();
        realm.Logger = log;
        var bob = realm.Connect("bob");

        Assert.False(await realm.Hub(bob).LoadCharacter("Nobodychar"));
        Assert.Empty(log.Security);
    }

    [Fact]
    public async Task OwnCharacter_IsNotLogged()
    {
        using var realm = await HubHarness.CreateAsync();
        var log = new CapturingLogger<GameHub>();
        realm.Logger = log;
        var bob = realm.Connect("bob");

        Assert.True(await realm.Hub(bob).LoadCharacter("Bobchar"));
        Assert.Empty(log.Security);
    }

    // ── REST helpers (Guilds, Friends, Blocks) share this ────────────────────

    [Fact]
    public async Task ForeignCharacterOverRest_IsLogged_TypoIsNot()
    {
        _db.Db.Characters.Add(new Character { UserId = "alice", Name = "Alicia" });
        await _db.Db.SaveChangesAsync();
        var log = new CapturingLogger<object>();

        await SecurityLog.NoteForeignCharacterAsync(_db.Db, log, "bob", "Alicia", "rest:guilds");
        await SecurityLog.NoteForeignCharacterAsync(_db.Db, log, "bob", "Alicai", "rest:guilds");
        await SecurityLog.NoteForeignCharacterAsync(_db.Db, log, "alice", "Alicia", "rest:guilds");

        Assert.Equal(["security realm.character.not_owner user=bob outcome=refused detail=rest:guilds;Alicia"], log.Security);
    }

    // ── Operator API ─────────────────────────────────────────────────────────

    private (AdminController Controller, CapturingLoggerFactory Logs) Admin(string remoteIp, string? secret, string method,
        string path = "/api/admin/characters/alice")
    {
        var db = _db.NewContext();
        var controller = new AdminController(db, _config, new BanService(db), new CharacterPresenceService(),
            new SessionRevocationService(_services.GetRequiredService<IServiceScopeFactory>(), _config), hub: null!,
            new CharacterSessionService(), new Myria.Server.ClassicRealm.Repositories.SqlCharacterRepository(db), new CharacterLocks());
        var logs = new CapturingLoggerFactory();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(logs).BuildServiceProvider()
        };
        http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        http.Connection.LocalIpAddress = IPAddress.Parse("127.0.0.1");
        http.Request.Method = method;
        http.Request.Path = path;
        if (secret is not null) http.Request.Headers["X-Internal-Secret"] = secret;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, logs);
    }

    [Theory]
    [InlineData("203.0.113.9", Secret, "not-on-this-machine")]
    [InlineData("127.0.0.1", null, "no-secret")]
    [InlineData("127.0.0.1", "guessed-secret", "wrong-secret")]
    public async Task RefusedOperatorCall_IsAWarning_WithoutSecretOrIp(string ip, string? secret, string reason)
    {
        var (controller, logs) = Admin(ip, secret, "DELETE");

        Assert.IsType<ForbidResult>(await controller.DeleteAllForUser("alice"));

        var line = Assert.Single(logs.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal($"security realm.admin.denied user=- outcome=refused detail={reason};DELETE:/api/admin/characters/alice",
            line.Message);
        Assert.DoesNotContain("203.0.113.9", line.Message);
        Assert.DoesNotContain(Secret, line.Message);
    }

    [Fact]
    public async Task AllowedOperatorChange_IsInformation()
    {
        var (controller, logs) = Admin("127.0.0.1", Secret, "DELETE");
        await controller.DeleteAllForUser("alice");

        var line = Assert.Single(logs.Lines, l => l.Message.StartsWith("security "));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("security realm.admin.call user=- outcome=allowed detail=DELETE:/api/admin/characters/alice", line.Message);
    }

    [Fact]
    public async Task AllowedOperatorRead_IsNotLogged()
    {
        var (controller, logs) = Admin("127.0.0.1", Secret, "GET", "/api/admin/accounts/alice");
        await controller.GetAccount("alice");
        Assert.DoesNotContain(logs.Lines, l => l.Message.StartsWith("security "));
    }

    // ── Tokens ───────────────────────────────────────────────────────────────

    [Fact]
    public void TokenRejectionReasons_AreShortAndFixed()
    {
        Assert.Equal("expired", SecurityLog.TokenRejectionReason(new SecurityTokenExpiredException("secret details")));
        Assert.Equal("invalid-signature", SecurityLog.TokenRejectionReason(new SecurityTokenInvalidSignatureException()));
        Assert.Equal("invalid-signature", SecurityLog.TokenRejectionReason(new SecurityTokenSignatureKeyNotFoundException()));
        Assert.Equal("wrong-issuer", SecurityLog.TokenRejectionReason(new SecurityTokenInvalidIssuerException()));
        Assert.Equal("wrong-audience", SecurityLog.TokenRejectionReason(new SecurityTokenInvalidAudienceException()));
        Assert.Equal("malformed", SecurityLog.TokenRejectionReason(new ArgumentException("eyJhbGciOi...")));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("a b", "a_b")]
    [InlineData("x\r\nsecurity realm.admin.call", "x__security_realm.admin.call")]
    public void Safe_MakesOneToken(string? input, string expected) =>
        Assert.Equal(expected, SecurityLog.Safe(input));
}
