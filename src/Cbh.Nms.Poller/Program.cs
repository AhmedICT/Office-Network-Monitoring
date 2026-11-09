using Cbh.Nms.Poller;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CBH-NMS Central Poller");

var connString = builder.Configuration.GetConnectionString("Nms")
    ?? throw new InvalidOperationException("ConnectionStrings:Nms is not configured");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connString));
builder.Services.AddHostedService<PollerService>();

builder.Build().Run();
