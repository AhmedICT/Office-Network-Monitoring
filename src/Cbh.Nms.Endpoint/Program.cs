using Cbh.Nms.Endpoint;
using Cbh.Nms.Endpoint.Activity;
using Cbh.Nms.Endpoint.Collectors;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CBH-NMS Endpoint Agent");
builder.Services.Configure<EndpointOptions>(builder.Configuration.GetSection("Endpoint"));

string Data(IServiceProvider sp, string file) => Path.Combine(sp.GetRequiredService<IOptions<EndpointOptions>>().Value.DataDirectory, file);
builder.Services.AddSingleton(sp => new Outbox(Data(sp, "outbox.db")));
builder.Services.AddSingleton(sp => new CredentialStore(Data(sp, "endpoint.key")));
builder.Services.AddSingleton<HealthCollector>();
builder.Services.AddSingleton<NetworkCollector>();
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<ActivityAggregator>();
builder.Services.AddSingleton<ActivityHub>();
builder.Services.AddHttpClient<HoClient>((sp, c) =>
{
    var url = sp.GetRequiredService<IOptions<EndpointOptions>>().Value.HoBaseUrl;
    if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Endpoint:HoBaseUrl is not set");
    c.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHostedService<EndpointService>();

builder.Build().Run();
