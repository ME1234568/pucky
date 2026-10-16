using Microsoft.AspNetCore.StaticFiles;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pucky.App.Devices;
using Pucky.App.Output;
using Pucky.App.Profiles;
using Pucky.App.Services;
using Pucky.Core.Mapping;

const string DefaultUrl = "http://127.0.0.1:27182";

var background = args.Any(arg => arg is "--background" or "--minimized" or "--no-window");
UiWindowLauncher.LaunchedInBackground = background;

// A second launch (for example from a desktop shortcut while Pucky already
// runs in the background) opens the existing instance's window and exits
// instead of failing to bind the port.
if (await TryHandOffToRunningInstanceAsync(
        Environment.GetEnvironmentVariable("PUCKY_URL") ?? DefaultUrl,
        openWindow: !background))
{
    return 0;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(arg => !arg.StartsWith("--background", StringComparison.Ordinal) &&
                             arg is not ("--minimized" or "--no-window")).ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls(builder.Configuration["PUCKY_URL"] ?? DefaultUrl);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IControllerTransport, HidSharpControllerTransport>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<VirtualOutputFactory>();
builder.Services.AddSingleton<HandOffMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<HandOffMonitor>());
builder.Services.AddSingleton<ControllerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ControllerService>());
builder.Services.AddSingleton<UiWindowLauncher>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<UiWindowLauncher>());
if (OperatingSystem.IsWindows())
{
    builder.Services.AddHostedService<WindowsTrayIcon>();
}

var app = builder.Build();
var liveJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
liveJson.Converters.Add(new JsonStringEnumConverter());
app.Use(async (context, next) =>
{
    // The API can type keys and move the mouse, so only the local UI may
    // change state. Browsers always send Origin/Sec-Fetch-Site on
    // cross-site requests, including "simple" POSTs that skip CORS.
    if (!HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !IsSameOriginRequest(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Cross-origin requests are not allowed." });
        return;
    }

    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    await next(context);
});
app.UseDefaultFiles();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".avif"] = "image/avif";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapGet("/api/status", (ControllerService service) => service.GetStatus());
app.MapGet("/api/live", async (
    HttpContext context,
    ControllerService service,
    CancellationToken cancellationToken) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    await context.Response.StartAsync(cancellationToken);

    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        long lastSent = -1;
        var lastWrite = DateTime.MinValue;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var frame = service.GetLiveFrame();
            // Skip unchanged frames, but keep a heartbeat so the UI can
            // notice a dropped connection.
            if (frame.ReportsReceived == lastSent &&
                DateTime.UtcNow - lastWrite < TimeSpan.FromSeconds(1))
            {
                continue;
            }

            lastSent = frame.ReportsReceived;
            lastWrite = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(frame, liveJson);
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
app.MapPost("/api/quit", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.NoContent();
});
app.MapPost("/api/window/open", (UiWindowLauncher windows) =>
{
    windows.OpenWindow();
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
    try
    {
        var saved = await store.SaveAsync(profile, token);
        return Results.Created($"/api/profiles/{saved.Id}", saved);
    }
    catch (ProfileValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
app.MapPost("/api/profiles/{id}/activate", async (
    string id,
    ProfileStore store,
    CancellationToken token) =>
{
    var profile = await store.ActivateAsync(id, token);
    return profile is null ? Results.NotFound(new { error = "Profile not found." }) : Results.Ok(profile);
});
app.MapDelete("/api/profiles/{id}", async (
    string id,
    ProfileStore store,
    CancellationToken token) =>
    id == ProfileStore.DefaultProfileId
        ? Results.BadRequest(new { error = "The default profile cannot be deleted." })
        : await store.DeleteAsync(id, token)
            ? Results.NoContent()
            : Results.NotFound(new { error = "Profile not found." }));
app.MapGet("/api/settings", (SettingsStore settings) => SettingsView(settings.Current));
app.MapPut("/api/settings", async (
    SettingsUpdate update,
    SettingsStore settings,
    CancellationToken token) =>
{
    if (update.OutputType is { } outputType && !VirtualOutputFactory.Supports(outputType))
    {
        return Results.BadRequest(new { error = $"{outputType} output is not available on this platform." });
    }

    if (update.LaunchAtLogin is { } launchAtLogin && launchAtLogin != AutoStart.IsEnabled())
    {
        try
        {
            AutoStart.SetEnabled(launchAtLogin);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or
                                       UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    var updated = await settings.UpdateAsync(current => current with
    {
        OutputType = update.OutputType ?? current.OutputType,
        HandOffEnabled = update.HandOffEnabled ?? current.HandOffEnabled,
        HandOffProcesses = update.HandOffProcesses ?? current.HandOffProcesses,
        StartInBackground = update.StartInBackground ?? current.StartInBackground
    }, token);
    return Results.Ok(SettingsView(updated));
});

app.MapFallbackToFile("index.html");
await app.RunAsync();
return 0;

static object SettingsView(AppSettings settings) => new
{
    settings.OutputType,
    SupportedOutputTypes = VirtualOutputFactory.SupportedTypes(),
    settings.HandOffEnabled,
    settings.HandOffProcesses,
    settings.StartInBackground,
    LaunchAtLogin = AutoStart.IsEnabled(),
    LaunchAtLoginSupported = AutoStart.IsSupported
};

static bool IsSameOriginRequest(HttpRequest request)
{
    var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
    if (fetchSite is "cross-site" or "same-site")
    {
        return false;
    }

    var origin = request.Headers.Origin.ToString();
    if (string.IsNullOrEmpty(origin))
    {
        // Non-browser clients (curl, a second Pucky launch) send no Origin.
        return true;
    }

    return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
           uri.IsLoopback &&
           uri.Port == request.Host.Port;
}

static async Task<bool> TryHandOffToRunningInstanceAsync(string configuredUrl, bool openWindow)
{
    var url = configuredUrl.Split(';', StringSplitOptions.RemoveEmptyEntries)[0]
        .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)
        .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
        .TrimEnd('/');
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        var status = await client.GetFromJsonAsync<JsonElement>($"{url}/api/status");
        if (!status.TryGetProperty("version", out _))
        {
            return false;
        }

        if (openWindow)
        {
            await client.PostAsync($"{url}/api/window/open", content: null);
        }
        Console.WriteLine($"Pucky is already running at {url}.");
        return true;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or NotSupportedException or InvalidOperationException)
    {
        return false;
    }
}

internal sealed record SettingsUpdate(
    GamepadOutputType? OutputType,
    bool? HandOffEnabled,
    List<string>? HandOffProcesses,
    bool? StartInBackground,
    bool? LaunchAtLogin);
