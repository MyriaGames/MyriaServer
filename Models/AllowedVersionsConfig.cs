namespace Myria.Server.Realm.Models
{
    /// <summary>
    /// Client versions this Realm server currently accepts. Loaded once at startup from
    /// Data/allowed_versions.json (same pattern as <see cref="GuildConfig"/>) rather than
    /// comparing solely against this assembly's own embedded version, so an operator can
    /// hand-widen the list (e.g. to allow both an old and a new build during a rollout)
    /// without rebuilding or redeploying the server binary.
    /// </summary>
    public class AllowedVersionsConfig
    {
        public List<string> AllowedClientVersions { get; set; } = new();

        public bool IsAllowed(string? clientVersion) =>
            clientVersion != null && AllowedClientVersions.Contains(clientVersion);
    }
}
