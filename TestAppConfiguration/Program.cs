using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using System.Text;

Timer tmr = null!;
bool appConfigurationOk = false;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

void ConfigureAppConfiguration()
{
    builder!.Configuration.AddAzureAppConfiguration(options =>
    {
        if (!string.IsNullOrEmpty(builder.Configuration["AppConfig"]))
        {
            options.Connect(builder.Configuration["AppConfig"]);
        }
        else if (Uri.TryCreate(builder.Configuration["Endpoints:AppConfig"], UriKind.Absolute, out var endpoint))
        {
            options.Connect(endpoint, new DefaultAzureCredential());
        }

        options.ConfigureRefresh(refresh =>
        {
            // All configuration values will be refreshed if the sentinel key changes.
            refresh.Register("ReturnedName", refreshAll: true);

        })
        .UseFeatureFlags(config =>
        {
            config.CacheExpirationInterval = TimeSpan.FromSeconds(30);
        });
    });

    appConfigurationOk = true;
}

var deferredLogging = new List<Action<ILogger>>();

try
{
    ConfigureAppConfiguration();
}
catch (AggregateException ae) when (ae.InnerException is Azure.RequestFailedException)
{
    deferredLogging.Add(x => x.LogWarning("Network error while trying to Configure App Configuration. Retrying in 10 seconds."));
}
catch (Exception ex)
{
    deferredLogging.Add(x => x.LogWarning(ex, "Unable to Configure App Configuration at startup"));
}

builder.Services.AddAzureAppConfiguration();
builder.Services.AddFeatureManagement();

var app = builder.Build();

deferredLogging.ForEach(x => x(app.Logger));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<DeferredAzureAppConfigurationRefreshMiddleware>();

if (!appConfigurationOk)
{
    const int RETRY_PERIOD_S = 10;

    tmr = new Timer(state =>
    {
        try
        {
            ConfigureAppConfiguration();
            app.Logger.LogInformation("AppConfiguration configured successfully!");
            tmr.Dispose();
        }
        catch (AggregateException ae) when (ae.InnerException is Azure.RequestFailedException)
        {
            app.Logger.LogWarning("Network error while trying to Configure App Configuration. Retrying in 10 seconds.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Unexpected error while trying to Configure App Configuration. Retrying in 10 seconds.");
        }
    }, null, RETRY_PERIOD_S * 1000, RETRY_PERIOD_S * 1000);
}

app.UseHttpsRedirection();


app.MapGet("/feature", async ([FromServices] IFeatureManager features, [FromServices] IConfiguration config) =>
{
    var f1 = await features.IsEnabledAsync("F1");
    var f2 = await features.IsEnabledAsync("F2");
    var f3 = await features.IsEnabledAsync("F3");

    return new
    {
        Name = config["ReturnedName"],
        F1Enabled = f1,
        F2Enabled = f2,
        F3Enabled = f3
    };
})
.WithName("GetFeature");

app.Run();


internal class DeferredAzureAppConfigurationRefreshMiddleware
{
    private readonly RequestDelegate _next;

    private readonly IConfiguration _config;
    private readonly IServiceProvider _sp;
    private IConfigurationRefresherProvider? _refresherProvider = null;

    public IEnumerable<IConfigurationRefresher> Refreshers { get; private set; } = Enumerable.Empty<IConfigurationRefresher>();

    public DeferredAzureAppConfigurationRefreshMiddleware(RequestDelegate next, IServiceProvider sp, IConfiguration config)
    {
        _next = next;
        _config = config;
        _sp = sp;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_refresherProvider == null)
        {
            var cr = _config as IConfigurationRoot;

            if (cr?.Providers?.Any(x => x.GetType().Name == "AzureAppConfigurationProvider") ?? false)
            {
                _refresherProvider = _sp.GetService<IConfigurationRefresherProvider>();

                if (_refresherProvider != null)
                {
                    Refreshers = _refresherProvider.Refreshers;
                }
            }
        }
        
        foreach (IConfigurationRefresher refresher in Refreshers)
        {
            _ = refresher.TryRefreshAsync();
        }

        await _next(context).ConfigureAwait(continueOnCapturedContext: false);
    }
}