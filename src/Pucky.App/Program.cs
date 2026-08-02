using Microsoft.AspNetCore.StaticFiles;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pucky.App.Devices;
using Pucky.App.Output;
using Pucky.App.Profiles;
using Pucky.App.Services;
using Pucky.Core.Mapping;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls(
    builder.Configuration["PUCKY_URL"] ?? "http://127.0.0.1:27182");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalUi", policy =>
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (origin == "null")
                {
                    return true;
                }

                return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                       (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            })
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddSingleton<IControllerTransport, HidSharpControllerTransport>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<VirtualOutputFactory>();
builder.Services.AddSingleton<ControllerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ControllerService>());
builder.Services.AddHostedService<UiWindowLauncher>();

var app = builder.Build();
var liveJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
liveJson.Converters.Add(new JsonStringEnumConverter());
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    await next(context);
});
app.UseDefaultFiles();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".avif"] = "image/avif";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.UseCors("LocalUi");

app.MapGet("/api/status", (ControllerService service) => service.GetStatus());
app.MapGet("/api/live", async (
    HttpContext context,
    ControllerService service,
    CancellationToken cancellationToken) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    await context.Response.StartAsync(cancellationToken);

    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(service.GetLiveFrame(), liveJson);
            await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
});
app.MapPost("/api/enable", (ControllerService service) =>
{
    service.Enable();
    return Results.NoContent();
});
app.MapPost("/api/disable", (ControllerService service) =>
{
    service.Disable();
    return Results.NoContent();
});
app.MapPost("/api/vibration/test", (ControllerService service) =>
    service.TestVibration()
        ? Results.Ok(new { active = true })
        : Results.Conflict(new { error = "Connect a controller before testing vibration." }));
app.MapPost("/api/haptics/test", (ControllerService service) =>
    service.TestTrackpadHaptics()
        ? Results.Ok(new { active = true })
        : Results.Conflict(new { error = "Connect a controller before testing trackpad haptics." }));
app.MapGet("/api/profiles", (ProfileStore store, CancellationToken token) =>
    store.ListAsync(token));
app.MapPost("/api/profiles", async (
    MappingProfile profile,
    ProfileStore store,
    CancellationToken token) =>
{
    await store.SaveAsync(profile, token);
    return Results.Created($"/api/profiles/{profile.Id}", profile);
});
app.MapPost("/api/profiles/{id}/activate", async (
    string id,
    ProfileStore store,
    CancellationToken token) =>
{
    var profile = await store.ActivateAsync(id, token);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapFallbackToFile("index.html");
await app.RunAsync();
