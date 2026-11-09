using System.Security.Cryptography;
using System.Text.Json;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Agent;

public sealed record AgentCredential(int AgentId, int BranchId, string Key);

/// <summary>Agent key at rest, protected with Windows DPAPI (machine scope) per NFR-09.</summary>
public sealed class CredentialStore(string path)
{
    public AgentCredential? Load() => Dpapi.ReadJson<AgentCredential>(path);
    public void Save(AgentCredential cred) => Dpapi.WriteJson(path, cred);
}

/// <summary>Last config pulled from HO, so the agent keeps polling after a restart while HO is unreachable.
/// It holds SNMP secrets, hence DPAPI.</summary>
public sealed class ConfigCache(string path)
{
    public AgentConfig? Load()
    {
        try { return Dpapi.ReadJson<AgentConfig>(path); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    public void Save(AgentConfig config) => Dpapi.WriteJson(path, config);
}

internal static class Dpapi
{
    private static readonly byte[] Entropy = "CBH-NMS-Agent-v1"u8.ToArray();

    public static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The agent runs on Windows only (SRS §2.4)");
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        return JsonSerializer.Deserialize<T>(json);
    }

    public static void WriteJson<T>(string path, T value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The agent runs on Windows only (SRS §2.4)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), Entropy, DataProtectionScope.LocalMachine);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
