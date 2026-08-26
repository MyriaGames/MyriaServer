using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Myria.Lib.Core.Repositories;
using Myria.Lib.Core.Services;
using Myria.Server.Realm.Data;
using Myria.Server.Realm.Hubs;
using Myria.Server.Realm.Models;
using Myria.Server.Realm.Repositories;
using Myria.Server.Realm.Services;

var builder = WebApplication.CreateBuilder(args);

// Refuse to run in Production with the dev-only placeholder Jwt:Key from the committed
// appsettings.json — that file ships inside the public repo and the published server zip,
// so its value is not secret. The real one must come from an environment variable
// (Jwt__Key) or a local, gitignored appsettings.Production.json, and must be
// byte-identical to MyriaAuthServer's, since that's the shared trust mechanism.
// (Security:Pepper only matters to MyriaAuthServer, which does the password hashing —
// this realm doesn't use it, so it's not checked here.)
if (builder.Environment.IsProduction())
{
    var configuredJwtKey = builder.Configuration["Jwt:Key"];
    if (configuredJwtKey == "REPLACE_ME_DEV_PLACEHOLDER_KEY" || string.IsNullOrWhiteSpace(configuredJwtKey))
        throw new InvalidOperationException(
            "Jwt:Key is still the dev placeholder (or unset) while running in Production. " +
            "Set a real secret via the Jwt__Key environment variable before starting this realm.");

    var configuredAdminSecret = builder.Configuration["Admin:InternalSecret"];
    if (configuredAdminSecret == "REPLACE_ME_DEV_PLACEHOLDER_ADMIN_SECRET" || string.IsNullOrWhiteSpace(configuredAdminSecret))
        throw new InvalidOperationException(
            "Admin:InternalSecret is still the dev placeholder (or unset) while running in Production. " +
            "Set a real secret via the Admin__InternalSecret environment variable before starting this realm.");

    // Every request here carries the player's Bearer JWT (and character/guild data) - refuse
    // to serve that over plain HTTP. The committed appsettings.json only defines an Http
    // endpoint (fine for local dev on loopback); a real deployment MUST override
    // Kestrel:Endpoints to an Https endpoint with a real certificate via a gitignored
    // appsettings.Production.json.
    var httpsUrl = builder.Configuration["Kestrel:Endpoints:Https:Url"];
    var certPath = builder.Configuration["Kestrel:Endpoints:Https:Certificate:Path"];
    var certPass = builder.Configuration["Kestrel:Endpoints:Https:Certificate:Password"];
    if (string.IsNullOrWhiteSpace(httpsUrl) || string.IsNullOrWhiteSpace(certPath) || string.IsNullOrWhiteSpace(certPass))
        throw new InvalidOperationException(
            "No Kestrel:Endpoints:Https (with a Certificate:Path/Password) is configured while running " +
            "in Production. This realm would otherwise serve player data and Bearer tokens over plain " +
            "HTTP. Configure a real HTTPS certificate via appsettings.Production.json.");
    if (!File.Exists(Path.IsPathRooted(certPath) ? certPath : Path.Combine(AppContext.BaseDirectory, certPath)))
        throw new InvalidOperationException($"Kestrel:Endpoints:Https:Certificate:Path '{certPath}' does not exist.");
}
// Note: the dev-only Kestrel:Endpoints:Http entry deliberately lives in appsettings.Development.json,
// not here - configuration providers merge by key, so if it lived in this file (loaded in every
// environment) it would stay bound alongside the Https endpoint above even in Production. Verified
// empirically (on Myria.Server.Auth, which has the identical pattern): with it in the shared file,
// Production served plain HTTP on top of HTTPS despite the check above. Keeping it Development-only
// is what makes "Production requires HTTPS" actually mean HTTPS-only.

// Guild config — loaded from Data/guild_config.json at startup
var guildConfigPath = Path.Combine(AppContext.BaseDirectory, "Data", "guild_config.json");
var guildConfig = File.Exists(guildConfigPath)
    ? JsonSerializer.Deserialize<GuildConfig>(File.ReadAllText(guildConfigPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new GuildConfig()
    : new GuildConfig();
builder.Services.AddSingleton(guildConfig);

// Database — local SQLite file, no external server/connection required. The connection
// string's "Data Source" is resolved against AppContext.BaseDirectory (like guildConfigPath
// above) so it doesn't depend on the process's current working directory at launch.
var sqliteConnStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"));
if (!Path.IsPathRooted(sqliteConnStr.DataSource))
    sqliteConnStr.DataSource = Path.Combine(AppContext.BaseDirectory, sqliteConnStr.DataSource);
Directory.CreateDirectory(Path.GetDirectoryName(sqliteConnStr.DataSource)!);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(sqliteConnStr.ConnectionString));

// Repository abstractions — SQL implementations for the server
builder.Services.AddScoped<ICharacterRepository, SqlCharacterRepository>();

// Guild system
builder.Services.AddScoped<GuildService>();
builder.Services.AddScoped<RookieService>();
builder.Services.AddScoped<GuildPropertyService>();

// Real-time multiplayer
builder.Services.AddSingleton<CharacterPresenceService>();
builder.Services.AddSingleton<PartyService>();
builder.Services.AddSingleton<CharacterSessionService>();
builder.Services.AddSingleton<TradeService>();
builder.Services.AddScoped<PlayerShopService>();
builder.Services.AddSingleton<GroupCombatService>();
builder.Services.AddSignalR();

// JWT authentication
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
        // SignalR WebSocket connections can't send custom headers,
        // so the token is passed as ?access_token=... in the query string.
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers().AddJsonOptions(opt =>
    opt.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Myria.Server.Realm API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            []
        }
    });
});

var app = builder.Build();

// Startup version banner — reads the .installed_version marker update-production.sh writes
// after a deploy (there's no other way to know what's actually running, since this project's
// own <Version> in Myria.Server.Realm.csproj isn't bumped per release). Falls back to a clear "dev"
// label for local runs / manual deployments that never went through that script.
var versionMarkerPath = Path.Combine(AppContext.BaseDirectory, ".installed_version");
var runningVersion = File.Exists(versionMarkerPath)
    ? File.ReadAllText(versionMarkerPath).Trim()
    : "dev (no .installed_version marker — not deployed via update-production.sh)";
app.Logger.LogInformation("Myria.Server.Realm starting — version: {Version}", runningVersion);

// Apply pending DB migrations for the player/account data (SQLite)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Load all static game content (items, monsters, quests, rooms, ...) from the shared
// JSON files into in-memory services — same source and code path the WPF client uses.
// This is the sole source of truth for game content; nothing about it is persisted to
// the database, which only holds player/account state (characters, guilds, friendships).
GameService.InitializeGame();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// No app.UseHttpsRedirection() here: Production is now HTTPS-only at the Kestrel level (see
// the guard above) — there's no plain-HTTP endpoint left to redirect away from. Development
// keeps its plain Http endpoint from appsettings.json for local-loopback convenience.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<GameHub>("/hubs/game");

app.Run();
