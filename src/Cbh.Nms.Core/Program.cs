using Cbh.Nms.Core;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Inventory;
using Cbh.Nms.Core.Status;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var connString = builder.Configuration.GetConnectionString("Nms")
    ?? throw new InvalidOperationException("ConnectionStrings:Nms is not configured");
var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<NmsDb>(o => o.UseNpgsql(dataSource).UseSnakeCaseNamingConvention());

// Device credentials are encrypted with these keys (NFR-08); losing the folder makes SNMP secrets unreadable.
var dp = builder.Services.AddDataProtection().SetApplicationName("CBH-NMS");
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
{
    dp.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
    if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate()
    .AddScheme<AgentAuthOptions, AgentAuthHandler>(AgentAuthHandler.SchemeName,
        o => builder.Configuration.GetSection("Agent:Auth").Bind(o));
builder.Services.AddScoped<IClaimsTransformation, AppUserClaimsTransformation>();
builder.Services.AddAuthorization(AuthPolicies.Add);

builder.Services.AddProblemDetails();
builder.Services.AddRequestDecompression();   // agents gzip their batches (NFR-06)
builder.Services.AddMemoryCache();
builder.Services.AddSignalR();
builder.Services.AddScoped<BranchScope>();
builder.Services.AddSingleton<MetricIngestor>();
builder.Services.AddSingleton<SqlMigrator>();
builder.Services.AddHostedService<StatusEngine>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    await app.Services.GetRequiredService<SqlMigrator>()
        .MigrateAsync(Path.Combine(AppContext.BaseDirectory, "migrations"), CancellationToken.None);

// First run: with no users nobody could log in, so seed the configured AD account as Super Admin.
if (app.Configuration["Bootstrap:SuperAdminUser"] is { Length: > 0 } bootstrapUser)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NmsDb>();
    if (!await db.Users.AnyAsync())
    {
        db.Users.Add(new AppUser { UserName = bootstrapUser, DisplayName = bootstrapUser, Role = Cbh.Nms.Shared.Roles.SuperAdmin });
        await db.SaveChangesAsync();
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseRequestDecompression();
app.UseAuthentication();
app.UseAuthorization();

app.MapAgentApi();
var api = app.MapGroup("/api/v1");
api.MapInventoryApi();
api.MapMonitoringApi();
app.MapHub<StatusHub>("/hubs/status");

app.MapGet("/health", async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    try
    {
        await using var cmd = ds.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "Healthy", database = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "Unhealthy", database = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();

app.Run();
