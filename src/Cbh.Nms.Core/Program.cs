using Cbh.Nms.Core.Endpoints;
using Cbh.Nms.Core;
using Cbh.Nms.Core.Admin;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Alerting;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Incidents;
using Cbh.Nms.Core.Inventory;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Core.Reports;
using Cbh.Nms.Core.Status;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

// Users: a cookie session, created by Windows auth (/api/v1/auth/windows) or the LDAPS form (SCR-01).
// Agents: their own key scheme. APIs answer 401/403 instead of redirecting.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "cbhnms";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(10);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddNegotiate()
    .AddScheme<AgentAuthOptions, AgentAuthHandler>(AgentAuthHandler.SchemeName,
        o => builder.Configuration.GetSection("Agent:Auth").Bind(o))
    .AddScheme<AgentAuthOptions, EndpointAuthHandler>(EndpointAuthHandler.SchemeName,
        o => builder.Configuration.GetSection("Agent:Auth").Bind(o));
builder.Services.AddScoped<IClaimsTransformation, AppUserClaimsTransformation>();
builder.Services.AddAuthorization(AuthPolicies.Add);
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

builder.Services.AddProblemDetails();
builder.Services.AddRequestDecompression();   // agents gzip their batches (NFR-06)
builder.Services.AddMemoryCache();
builder.Services.AddSignalR();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BranchScope>();
builder.Services.AddSingleton<MetricIngestor>();
builder.Services.AddSingleton<SqlMigrator>();
builder.Services.AddSingleton<NmsDbClock>();

// Notifications (FR-AL-06, INT-03/04) and Task Management (INT-01).
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection("Sms"));
builder.Services.Configure<TaskManagementOptions>(builder.Configuration.GetSection("TaskManagement"));
builder.Services.AddScoped<NotificationPlanner>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient<ISmsGateway, HttpSmsGateway>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<TaskManagementClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<ReportService>();
builder.Services.AddSingleton<EndpointAlertSource>();

// Evaluation Engine and background jobs.
builder.Services.AddHostedService<StatusEngine>();
builder.Services.AddHostedService<AlertEngine>();
builder.Services.AddHostedService<IncidentEngine>();
builder.Services.AddHostedService<NotificationSender>();
builder.Services.AddHostedService<MonthlyReportJob>();
builder.Services.AddHostedService<EndpointHousekeeping>();

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
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapAgentApi();
app.MapEndpointAgentApi();
var api = app.MapGroup("/api/v1");
api.MapAuthApi();
api.MapInventoryApi();
api.MapInventoryExtraApi();
api.MapMonitoringApi();
api.MapAlertApi();
api.MapIncidentApi();
api.MapMaintenanceApi();
api.MapReportApi();
api.MapAdminApi();
api.MapEndpointApi();
app.MapHub<StatusHub>("/hubs/status");

// GET /health: application, PostgreSQL, Central Poller and Evaluation Engine (SRS §7.2).
app.MapGet("/health", async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    try
    {
        await using var cmd = ds.CreateCommand("""
            SELECT (SELECT max(time) FROM mon.reachability WHERE time > now() - interval '1 day'),
                   (SELECT max(updated_at) FROM mon.status_current)
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var poller = r.IsDBNull(0) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(0);
        var engine = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
        var now = DateTimeOffset.UtcNow;
        var pollerOk = poller is { } p && now - p < TimeSpan.FromMinutes(2);
        var engineOk = engine is { } e && now - e < TimeSpan.FromMinutes(1);
        var body = new
        {
            status = pollerOk && engineOk ? "Healthy" : "Degraded",
            database = "ok",
            centralPoller = new { ok = pollerOk, lastPollUtc = poller },
            evaluationEngine = new { ok = engineOk, lastRunUtc = engine },
        };
        return Results.Json(body, statusCode: pollerOk && engineOk ? 200 : 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "Unhealthy", database = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();

// React SPA (SCR-01..09) is built into wwwroot; unknown non-API paths serve index.html.
app.MapFallbackToFile("index.html");

app.Run();
