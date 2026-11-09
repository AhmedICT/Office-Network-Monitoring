using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Agent;

/// <summary>Thin client for /agent/v1. Outbound HTTPS only (SRS §2.4); bodies are gzipped (NFR-06).</summary>
public sealed class HoClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private AgentCredential? _cred;

    public void UseCredential(AgentCredential cred)
    {
        _cred = cred;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Agent", $"{cred.AgentId}:{cred.Key}");
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("agent/v1/register", req, Json, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<RegisterResponse>(Json, ct))!;
    }

    /// <summary>Returns null when the server answers 304 (config unchanged).</summary>
    public async Task<AgentConfig?> GetConfigAsync(int? knownVersion, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "agent/v1/config");
        if (knownVersion is { } v) req.Headers.TryAddWithoutValidation("If-None-Match", $"\"v{v}\"");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotModified) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<AgentConfig>(Json, ct);
    }

    public async Task<SendResult> PostMetricsAsync(MetricBatch batch, CancellationToken ct)
    {
        using var res = await http.SendAsync(Gzipped(HttpMethod.Post, "agent/v1/metrics", batch), ct);
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
            return new SendResult(null, res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30));
        res.EnsureSuccessStatusCode();
        var ack = await res.Content.ReadFromJsonAsync<MetricAck>(Json, ct);
        return new SendResult(ack?.AckedUpToSeq, null);
    }

    public async Task PostHeartbeatAsync(Heartbeat hb, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("agent/v1/heartbeat", hb, Json, ct);
        res.EnsureSuccessStatusCode();
    }

    public int AgentId => _cred?.AgentId ?? throw new InvalidOperationException("Agent not registered");

    private static HttpRequestMessage Gzipped<T>(HttpMethod method, string path, T body)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, body, Json);
        ms.Position = 0;
        var content = new StreamContent(ms);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return new HttpRequestMessage(method, path) { Content = content };
    }
}

public sealed record SendResult(long? AckedUpToSeq, TimeSpan? RetryAfter);
