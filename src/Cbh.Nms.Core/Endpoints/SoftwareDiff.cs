using System.Text;

namespace Cbh.Nms.Core.Endpoints;

public sealed record InstalledSoftware(string Name, string Version);
public sealed record SoftwareChange(string Action, string Name, string? Version, string? OldVersion);

/// <summary>Turns two inventory snapshots of one PC into INSTALLED / UPDATED / REMOVED events.</summary>
public static class SoftwareDiff
{
    public static List<SoftwareChange> Compute(IReadOnlyCollection<InstalledSoftware> before, IReadOnlyCollection<InstalledSoftware> after)
    {
        // The first inventory is the baseline: everything on the PC would otherwise show up as "installed today".
        if (before.Count == 0) return [];

        var changes = new List<SoftwareChange>();
        var old = before.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Version).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var now = after.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Version).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var (name, versions) in now)
        {
            if (!old.TryGetValue(name, out var oldVersions))
            {
                changes.AddRange(versions.Select(v => new SoftwareChange("INSTALLED", name, v, null)));
                continue;
            }
            var added = versions.Except(oldVersions, StringComparer.OrdinalIgnoreCase).ToList();
            var gone = oldVersions.Except(versions, StringComparer.OrdinalIgnoreCase).ToList();
            // Same product, one version replaced by another = an update.
            if (added.Count == 1 && gone.Count == 1) changes.Add(new("UPDATED", name, added[0], gone[0]));
            else
            {
                changes.AddRange(added.Select(v => new SoftwareChange("INSTALLED", name, v, null)));
                changes.AddRange(gone.Select(v => new SoftwareChange("REMOVED", name, v, null)));
            }
        }
        foreach (var (name, versions) in old)
            if (!now.ContainsKey(name))
                changes.AddRange(versions.Select(v => new SoftwareChange("REMOVED", name, v, null)));
        return changes;
    }
}

/// <summary>Wildcard ("*" any text, "?" one character) → PostgreSQL ILIKE pattern, with % and _ escaped.</summary>
public static class Wildcard
{
    public static string ToLike(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        foreach (var c in pattern.Trim())
            sb.Append(c switch
            {
                '*' => "%",
                '?' => "_",
                '%' or '_' or '\\' => "\\" + c,
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
