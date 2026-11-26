using System.Net;

namespace Myria.Server.ClassicRealm.Services
{
    /// <summary>
    /// Plain-HTTP Kestrel endpoints that listen beyond this machine. Behind a reverse proxy on the same
    /// host (Caddy terminates TLS) a plain endpoint is fine - but only on loopback; on 0.0.0.0 just the
    /// firewall keeps passwords/tokens in clear text off the network (security review 2026-11-26, A02 F2).
    /// </summary>
    public static class PlainHttpEndpoints
    {
        public static IReadOnlyList<string> ReachableFromNetwork(IConfiguration config)
        {
            var urls = config.GetSection("Kestrel:Endpoints").GetChildren()
                .Select(e => e["Url"])
                .Concat((config["urls"] ?? "").Split(';'))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!.Trim());
            return urls.Where(IsPlainAndNotLoopback).Distinct().ToList();
        }

        public static bool IsPlainAndNotLoopback(string url)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return false;
            // Kestrel's wildcards (*, +) aren't valid URI hosts - they mean "every interface".
            var rest = url["http://".Length..];
            var host = rest.StartsWith('[') ? rest[..(rest.IndexOf(']') + 1)] : rest.Split(':', '/')[0];
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return false;
            return !(IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        }
    }
}
