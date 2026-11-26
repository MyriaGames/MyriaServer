using Microsoft.Extensions.Logging;
using Myria.Server.ClassicRealm.Hubs;
using Myria.Server.ClassicRealm.Services;
using Xunit;

namespace Myria.Server.ClassicRealm.Tests;

/// <summary>Security review 2026-11-26, A05 F1: client-sent values must not forge log entries.</summary>
public class LogSafeTests
{
    [Theory]
    [InlineData("x\nfake entry", "x?fake entry")]
    [InlineData("a\r\nb\tc\u001b[31m", "a??b?c?[31m")]
    [InlineData("line\u2028sep", "line?sep")]
    [InlineData("Alicechar", "Alicechar")]
    public void Text_ReplacesLineBreaksAndControlCharacters(string input, string expected) =>
        Assert.Equal(expected, LogSafe.Text(input));

    [Fact]
    public void Text_TruncatesLongValues_AndSaysHowLongTheyWere()
    {
        var result = LogSafe.Text(new string('a', 5000));
        Assert.StartsWith(new string('a', LogSafe.MaxLength) + "...", result);
        Assert.EndsWith("(5000 chars)", result);
    }

    [Fact]
    public void Text_HandlesNull() => Assert.Equal("(null)", LogSafe.Text(null));

    private sealed class CapturingLogger : ILogger<GameHub>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task RejectedSetCharacterName_IsLoggedWithoutLineBreaks()
    {
        using var realm = await HubHarness.CreateAsync();
        var logger = new CapturingLogger();
        realm.Logger = logger;
        var bob = realm.Connect("bob");
        await realm.Hub(bob).LoadCharacter("Bobchar");

        await realm.Hub(bob).SetCharacterName("Alicechar\n2026-11-26 info: Admin banned Bobchar");

        var message = Assert.Single(logger.Messages, m => m.Contains("tried to set character name"));
        Assert.DoesNotContain('\n', message);
        Assert.Contains("Alicechar?2026-11-26", message);
    }
}
