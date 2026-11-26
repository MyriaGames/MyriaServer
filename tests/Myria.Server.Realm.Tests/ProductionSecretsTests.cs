using Myria.Server.Realm.Services;
using Xunit;

namespace Myria.Server.Realm.Tests;

/// <summary>Security review 2026-11-26, A10 F1: every shipped placeholder must stop Production.</summary>
public class ProductionSecretsTests
{
    [Theory]
    [InlineData("REPLACE_ME_DEV_PLACEHOLDER_KEY_AT_LEAST_32_BYTES_LONG")]   // the value actually shipped
    [InlineData("REPLACE_ME_DEV_PLACEHOLDER_ADMIN_SECRET")]
    [InlineData("  replace_me_anything")]
    [InlineData("")]
    [InlineData(null)]
    public void Placeholders_AreRecognised(string? value) => Assert.True(ProductionSecrets.IsPlaceholder(value));

    [Fact]
    public void ARealSecret_IsNot() => Assert.False(ProductionSecrets.IsPlaceholder("q8Zt7m0Vx3Lk9pR2sW5yB1nC6dF4gH0jK8eA"));

    [Fact]
    public void TheShippedAppsettingsKey_IsRecognised()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.json"));
        var key = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("Jwt").GetProperty("Key").GetString();
        Assert.True(ProductionSecrets.IsPlaceholder(key));
    }
}
