using System.Net.Http.Json;
using System.Text.Json;
using Cbh.Nms.Core.Data;

namespace Cbh.Nms.Core.Incidents;

public sealed class TaskManagementOptions
{
    /// <summary>Base URL of PDCL IT Task Management. Empty disables INT-01.</summary>
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Key Task Management must send in X-Api-Key on callbacks.</summary>
    public string? CallbackApiKey { get; set; }
    /// <summary>Source IPs allowed to call the callback; empty = any.</summary>
    public List<string> CallbackAllowedIps { get; set; } = [];
    public string CreatePath { get; set; } = "api/tickets";
    public string UpdatePath { get; set; } = "api/tickets/{id}";
    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>
/// INT-01 client. The Task Management API contract is not yet documented (SRS §1.4), so this assumes a
/// minimal REST shape — POST create returns {"id": "..."}, PATCH update takes status/assignee — with
/// paths configurable. Adjust <see cref="TaskManagementOptions"/> once the vendor API is confirmed.
/// </summary>
public sealed class TaskManagementClient(HttpClient http, Microsoft.Extensions.Options.IOptions<TaskManagementOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TaskManagementOptions _opt = options.Value;
    public bool Enabled => _opt.Enabled;

    public async Task<string> CreateTicketAsync(Incident i, string branchCode, string? assigneeUserName, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, _opt.CreatePath);
        req.Content = JsonContent.Create(new
        {
            externalId = i.IncidentNo,
            title = $"[{branchCode}] {i.Title}",
            description = $"CBH-NMS incident {i.IncidentNo}, severity {i.Severity}, detected {i.DetectedAt:u}.",
            priority = i.Severity,
            branchCode,
            assignee = assigneeUserName,
        }, options: Json);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.ToString()
            : throw new InvalidOperationException("Task Management response has no 'id'");
    }

    public async Task UpdateTicketAsync(Incident i, string? assigneeUserName, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Patch, _opt.UpdatePath.Replace("{id}", Uri.EscapeDataString(i.TaskTicketId!)));
        req.Content = JsonContent.Create(new
        {
            status = i.Status, assignee = assigneeUserName, rootCause = i.RootCause, resolutionNote = i.ResolutionNote,
        }, options: Json);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var baseUrl = _opt.BaseUrl!.EndsWith('/') ? _opt.BaseUrl : _opt.BaseUrl + "/";
        var req = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        if (_opt.ApiKey is { Length: > 0 } key) req.Headers.Add("X-Api-Key", key);
        return req;
    }
}
