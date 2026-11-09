using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cbh.Nms.Core.Status;

/// <summary>/hubs/status (SRS §7.5). Clients only join the groups their branch scope allows.</summary>
[Authorize(Policy = AuthPolicies.Viewer)]
public sealed class StatusHub(BranchScope scope) : Hub
{
    public const string AllGroup = "all";
    public static string BranchGroup(int branchId) => $"branch:{branchId}";

    public override async Task OnConnectedAsync()
    {
        var allowed = await scope.AllowedAsync(Context.User!);
        if (allowed is null)
            await Groups.AddToGroupAsync(Context.ConnectionId, AllGroup);
        else
            foreach (var id in allowed)
                await Groups.AddToGroupAsync(Context.ConnectionId, BranchGroup(id));
        await base.OnConnectedAsync();
    }
}

public static class StatusHubExtensions
{
    /// <summary>Sends to unscoped users plus users scoped to this branch.</summary>
    public static Task SendForBranchAsync(this IHubContext<StatusHub> hub, int branchId, string method, object payload,
        CancellationToken ct = default) =>
        hub.Clients.Groups(StatusHub.AllGroup, StatusHub.BranchGroup(branchId)).SendAsync(method, payload, ct);
}
