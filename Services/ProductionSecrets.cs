namespace Myria.Server.ClassicRealm.Services
{
    // Every committed placeholder secret starts with "REPLACE_ME". Comparing against one exact
    // string drifted: the check looked for "REPLACE_ME_DEV_PLACEHOLDER_KEY" while appsettings.json
    // ships "REPLACE_ME_DEV_PLACEHOLDER_KEY_AT_LEAST_32_BYTES_LONG", so a forgotten Jwt__Key let
    // Production run with a key anyone can read on GitHub (security review 2026-11-26, A10 F1).
    public static class ProductionSecrets
    {
        public static bool IsPlaceholder(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("REPLACE_ME", StringComparison.OrdinalIgnoreCase);
    }
}
