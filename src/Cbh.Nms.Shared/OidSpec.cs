using System.Globalization;

namespace Cbh.Nms.Shared;

/// <summary>
/// An OID from a vendor SNMP profile, optionally with a value transform:
/// "OID|eq:5" (1 when equal, else 0), "OID|div:100", "OID|mul:N". Shared so HO validates what agents will parse.
/// </summary>
public readonly record struct OidSpec(string Oid, string? Op, double Arg)
{
    public static OidSpec Parse(string spec)
    {
        var parts = spec.Split('|', 2);
        if (parts.Length == 1) return new OidSpec(parts[0].Trim(), null, 0);
        var op = parts[1].Split(':', 2);
        if (op.Length != 2 || !double.TryParse(op[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var arg))
            throw new FormatException($"Bad OID transform '{spec}'");
        return new OidSpec(parts[0].Trim(), op[0].Trim(), arg);
    }

    public double Apply(double v) => Op switch
    {
        null => v,
        "eq" => v == Arg ? 1 : 0,
        "div" => v / Arg,
        "mul" => v * Arg,
        _ => throw new FormatException($"Unknown OID transform '{Op}'"),
    };
}
