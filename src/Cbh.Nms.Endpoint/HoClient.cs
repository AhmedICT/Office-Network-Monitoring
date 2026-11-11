using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint;

public sealed record EndpointCredential(int EndpointId, int BranchId, string Key);

/// <summary>Client for /endpoint/v1. Outbound HTTPS only; bodies gzipped.</summary>
public sealed class HoClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void UseCredential(EndpointCredential c) =>
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Endpoint", $"{c.EndpointId}:{c.Key}");

    public async Task<EndpointRegisterResponse> RegisterAsync(EndpointRegisterRequest req, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("endpoint/v1/register", req, Json, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<EndpointRegisterResponse>(Json, ct))!;
    }

    public async Task<EndpointConfig?> GetConfigAsync(CancellationToken ct)
    {
        using var res = await http.GetAsync("endpoint/v1/config", ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Endpoint key rejected (revoked?)");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<EndpointConfig>(Json, ct);
    }

    /// <summary>Posts already-serialised uploads. Reports and activity go as a JSON array; inventory as one object.</summary>
    public async Task PostRawAsync(string path, string json, CancellationToken ct)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(json));
        ms.Position = 0;
        var content = new StreamContent(ms);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        using var res = await http.PostAsync(path, content, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Endpoint key rejected (revoked?)");
        res.EnsureSuccessStatusCode();
    }
}

/// <summary>Endpoint key at rest, protected with DPAPI (machine scope); only SYSTEM/Administrators can read the folder.</summary>
public sealed class CredentialStore(string path)
{
    private static readonly byte[] Entropy = "CBH-NMS-Endpoint-v1"u8.ToArray();

    public EndpointCredential? Load()
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<EndpointCredential>(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    public void Save(EndpointCredential c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(c), Entropy, DataProtectionScope.LocalMachine));
        File.Move(tmp, path, overwrite: true);
    }
}
