using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
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
    builder.Services.AddAzureAppConfiguration();
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

builder.Services.AddFeatureManagement();

var app = builder.Build();

deferredLogging.ForEach(x => x(app.Logger));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (appConfigurationOk)
{
    app.UseAzureAppConfiguration();
}
else
{
    tmr = new Timer(state =>
    {
        try
        {
            ConfigureAppConfiguration();
            app.Logger.LogInformation("AppConfiguration configured successfully!");
            tmr.Dispose();
        }
        catch (InvalidOperationException)
        {
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
    }, null, 10000, 10000);
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
