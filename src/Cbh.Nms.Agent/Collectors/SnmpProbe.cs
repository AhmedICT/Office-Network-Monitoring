using System.Globalization;
using System.Net;
using Cbh.Nms.Shared;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace Cbh.Nms.Agent.Collectors;

/// <summary>
/// SNMPv3 authPriv GET (SRS §2.4: v1/v2c are never used). The vendor profile maps metric keys to OIDs;
/// an OID may carry a transform suffix:
///   "1.3.6.1.2.1.33.1.4.1.0|eq:5"  → 1 when the value equals 5, else 0 (UPS-MIB upsOutputSource = battery)
///   "1.3.6.1.2.1.1.3.0|div:100"    → value / 100 (sysUpTime ticks → seconds)
/// </summary>
public static class SnmpProbe
{
    public static async Task<Dictionary<string, double>> GetAsync(string ip, SnmpCredential cred,
        IReadOnlyDictionary<string, string> oids, int timeoutMs, CancellationToken ct)
    {
        var parsed = oids.ToDictionary(kv => kv.Key, kv => OidSpec.Parse(kv.Value));
        var endpoint = new IPEndPoint(IPAddress.Parse(ip), 161);
        var auth = CreateAuth(cred);
        var priv = CreatePriv(cred, auth);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = await discovery.GetResponseAsync(endpoint, cts.Token);

        var variables = parsed.Values.Select(s => new Variable(new ObjectIdentifier(s.Oid))).ToList();
        var request = new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
            new OctetString(cred.User), OctetString.Empty, variables, priv, Messenger.MaxMessageSize, report);
        var reply = await request.GetResponseAsync(endpoint, cts.Token);
        if (reply.Pdu().ErrorStatus.ToInt32() != 0)
            throw new SnmpException($"SNMP error {reply.Pdu().ErrorStatus} from {ip}");

        var byOid = reply.Pdu().Variables.ToDictionary(v => v.Id.ToString(), v => v.Data);
        var result = new Dictionary<string, double>();
        foreach (var (key, spec) in parsed)
            if (byOid.TryGetValue(spec.Oid, out var data) && ToDouble(data) is { } raw)
                result[key] = spec.Apply(raw);
        return result;
    }

    private static IAuthenticationProvider CreateAuth(SnmpCredential c) => c.AuthProtocol switch
    {
#pragma warning disable CS0618 // SHA-1 is what the deployed Fortinet/Cisco/MikroTik fleet supports (SRS §2.4)
        "SHA" => new SHA1AuthenticationProvider(new OctetString(c.AuthSecret)),
#pragma warning restore CS0618
        "SHA256" => new SHA256AuthenticationProvider(new OctetString(c.AuthSecret)),
        _ => throw new NotSupportedException($"Auth protocol {c.AuthProtocol}"),
    };

    private static IPrivacyProvider CreatePriv(SnmpCredential c, IAuthenticationProvider auth) => c.PrivProtocol switch
    {
        "AES" => new AESPrivacyProvider(new OctetString(c.PrivSecret), auth),
        "AES256" => new AES256PrivacyProvider(new OctetString(c.PrivSecret), auth),
        _ => throw new NotSupportedException($"Privacy protocol {c.PrivProtocol}"),
    };

    private static double? ToDouble(ISnmpData data) => data switch
    {
        Integer32 i => i.ToInt32(),
        Counter32 c => c.ToUInt32(),
        Gauge32 g => g.ToUInt32(),
        Counter64 c => c.ToUInt64(),
        TimeTicks t => t.ToUInt32(),
        OctetString s when double.TryParse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
