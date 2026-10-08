using System.Globalization;
using System.Net;

namespace ReflectionTimer.Core;

// Website identity is independent of a browser window, tab or page path.
// Parsing is local only: no DNS lookup, navigation or network request.
public static class FocusSites
{
    public static string CanonicalHost(string value) => TryCanonicalHost(value, out var host)
        ? host : throw new ArgumentException("Enter a website host or an http/https website address.");

    public static bool TryCanonicalHost(string? value, out string host)
    {
        host = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.Contains('\\')) return false;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var hasScheme = schemeEnd >= 0 && value[..schemeEnd].IndexOfAny(['/', '?', '#']) < 0;
        if (hasScheme && !value[..schemeEnd].Equals("http", StringComparison.OrdinalIgnoreCase)
            && !value[..schemeEnd].Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        var address = hasScheme ? value : "https://" + value;
        var authorityStart = address.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = address.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = address[authorityStart..(authorityEnd < 0 ? address.Length : authorityEnd)];
        if (authority.Length == 0 || authority.Contains('@') || authority.Contains('%')) return false;
        try {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
                || uri.Scheme is not ("http" or "https") || uri.Port is < 1 or > 65535) return false;
            string name;
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) {
                if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
                name = uri.HostNameType == UriHostNameType.IPv6 ? "[" + ip + "]" : ip.ToString();
            } else {
                if (uri.HostNameType != UriHostNameType.Dns) return false;
                name = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(uri.IdnHost).ToLowerInvariant();
                if (name.EndsWith("..", StringComparison.Ordinal)) return false;
                name = name.TrimEnd('.');
                if (name.Length is 0 or > 253 || name.Split('.').Any(label => label.Length is 0 or > 63
                    || label[0] == '-' || label[^1] == '-'
                    || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return false;
                // Common www aliases share an identity with the bare website.
                // Repeated prefixes are removed to keep normalization idempotent.
                while (name.StartsWith("www.", StringComparison.Ordinal) && name[4..].Contains('.')) name = name[4..];
                if (name.Length == 0) return false;
            }
            var portStart = authority[0] == '[' ? authority.IndexOf(']') + 1 : authority.LastIndexOf(':');
            var explicitPort = portStart >= 0 && portStart < authority.Length;
            if (explicitPort && (authority[portStart] != ':' || portStart + 1 == authority.Length
                || authority[(portStart + 1)..].Any(c => !char.IsAsciiDigit(c)))) return false;
            // A stored canonical host may retain :443 or :80 from a URL where
            // it was non-default. Do not discard an explicit typed host port.
            var port = explicitPort && (!hasScheme || !uri.IsDefaultPort) ? ":" + uri.Port : "";
            host = name + port;
            return true;
        } catch (ArgumentException) { return false; }
        catch (UriFormatException) { return false; }
    }

    public static bool Matches(string targetHost, string candidateUrlOrHost)
    {
        if (!TryCanonicalHost(targetHost, out var target) || !TryCanonicalHost(candidateUrlOrHost, out var candidate)) return false;
        var (targetName, targetPort) = Parts(target);
        var (candidateName, candidatePort) = Parts(candidate);
        if (targetPort != candidatePort) return false;
        if (targetName == candidateName) return true;
        if (IPAddress.TryParse(targetName.Trim('[', ']'), out _)
            || IPAddress.TryParse(candidateName.Trim('[', ']'), out _)) return false;
        return candidateName.EndsWith("." + targetName, StringComparison.Ordinal);
    }

    private static (string Host, string Port) Parts(string host)
    {
        var separator = host[0] == '[' ? host.IndexOf(']') + 1 : host.LastIndexOf(':');
        return separator >= 0 && separator < host.Length && host[separator] == ':'
            ? (host[..separator], host[separator..]) : (host, "");
    }
}
