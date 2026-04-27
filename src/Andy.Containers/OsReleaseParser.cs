namespace Andy.Containers;

/// <summary>
/// Parses the contents of <c>/etc/os-release</c> into a short
/// human label like "Debian 12" or "Alpine 3.19".
///
/// Conductor #871. The format is the freedesktop.org spec:
/// shell-style KEY=VALUE lines, values may be quoted. We pull
/// <c>NAME</c> + <c>VERSION_ID</c> and stitch them. If
/// <c>VERSION_ID</c> is missing (some rolling distros), we fall
/// back to <c>NAME</c> alone. If the input is unparseable,
/// returns null — the caller treats that as "OS unknown" and
/// renders nothing.
/// </summary>
public static class OsReleaseParser
{
    public static string? ParseLabel(string? osReleaseContents)
    {
        if (string.IsNullOrWhiteSpace(osReleaseContents))
        {
            return null;
        }

        string? name = null;
        string? versionId = null;

        foreach (var rawLine in osReleaseContents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());

            if (key == "NAME")
            {
                name = value;
            }
            else if (key == "VERSION_ID")
            {
                versionId = value;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return string.IsNullOrEmpty(versionId)
            ? name
            : $"{name} {versionId}";
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' || first == '\'') && first == last)
            {
                return value[1..^1];
            }
        }
        return value;
    }
}
