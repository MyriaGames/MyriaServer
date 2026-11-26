namespace Myria.Server.ClassicRealm.Services
{
    // For logging a value a client sent that was NOT validated (typically the very value being
    // rejected). The console logger writes values into the line unchanged and journald starts a new
    // entry at every line break, so a raw "\n" in such a value forges what looks like a separate,
    // genuine log entry (security review 2026-11-26, A05 F1). Values that already matched a database
    // row or a game entity don't need this.
    public static class LogSafe
    {
        public const int MaxLength = 60;

        /// <summary>Control characters (line breaks, tabs, escapes) become '?'; cut to <see cref="MaxLength"/>.</summary>
        public static string Text(string? value)
        {
            if (value is null) return "(null)";
            var cut = value.Length > MaxLength;
            var span = cut ? value.AsSpan(0, MaxLength) : value.AsSpan();
            var chars = new char[span.Length];
            for (var i = 0; i < span.Length; i++)
                chars[i] = char.IsControl(span[i]) || span[i] == (char)0x2028 || span[i] == (char)0x2029 ? '?' : span[i];
            return new string(chars) + (cut ? $"... ({value.Length} chars)" : "");
        }
    }
}
