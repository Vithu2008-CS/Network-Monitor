using System.Text;
using System.Text.Json;
using Flow;

if (args.Contains("--self-test")) { SelfTest.Run(); return; }
if (args.Contains("--restore-controls"))
{
    using var recoveryStore = new TrafficStore(Path.Combine(Environment.CurrentDirectory, "data", "usage.db"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await new WindowsRules().Clear(timeout.Token);
    foreach (var rule in recoveryStore.Rules()) recoveryStore.SaveRule(rule with { Enabled = false, Blocked = false });
    recoveryStore.LogAction("Recovery", "All applications", "Flow Windows controls removed and saved rules disabled.");
    Console.WriteLine("Flow app access restored. Saved rules disabled; history retained.");
    return;
}
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Flow requires Windows 10 or 11.");

var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--share").ToArray());
var access = new AccessPolicy(args.Contains("--share") ? AccessPolicy.FindLanAddress(builder.Configuration["share-address"]) : null);
builder.WebHost.UseUrls(access.ListenUrls);
builder.Services.AddSingleton(access);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton(new TrafficStore(Path.Combine(builder.Environment.ContentRootPath, "data", "usage.db")));
builder.Services.AddSingleton<RouterClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RouterClient>());
builder.Services.AddSingleton<NetworkDiagnostics>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkDiagnostics>());
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorService>());
builder.Services.AddSingleton<IWindowsRules, WindowsRules>();
builder.Services.AddSingleton<ControlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControlService>());
builder.Services.AddSingleton<StartupService>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!access.Allows(context))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/health", () => new { app = "Flow", status = "ok" });
app.MapPost("/api/stop", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () => { await Task.Delay(500); lifetime.StopApplication(); });
    return Results.Ok(new { success = true, message = "Monitor stopped. Restart with Start Monitor.cmd." });
});
app.MapGet("/api/state", (HttpContext context, MonitorService monitor) => monitor.Snapshot(access.Info(context)));
app.MapGet("/api/history", (string? range, string? start, string? end, TrafficStore store) =>
{
    try { return Results.Ok(store.History(range ?? "day", start, end)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (Microsoft.Data.Sqlite.SqliteException) { return Results.Json(new { message = "Local history is temporarily unavailable. Try Refresh again." }, statusCode: 503); }
});
app.MapGet("/api/export", (string? range, string? start, string? end, string? format, TrafficStore store) =>
{
    try
    {
        var history = store.History(range ?? "day", start, end);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var exportObj = new
            {
                exportedAt = DateTimeOffset.Now,
                range = history.Range,
                startDate = history.StartDate,
                endDate = history.EndDate,
                totals = history.Totals,
                daily = history.Daily,
                apps = history.Apps,
                storage = history.Storage
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(exportObj, new JsonSerializerOptions { WriteIndented = true });
            return Results.File(jsonBytes, "application/json", $"flow-{history.Range}-{DateTime.Now:yyyy-MM-dd}.json");
        }
        var text = new StringBuilder("Kind,Date,Program,Executable,Download bytes,Upload bytes\r\n");
        foreach (var a in history.Apps)
            text.AppendLine($"Application,,{Accounting.SafeCsv(a.Name)},{Accounting.SafeCsv(a.Path)},{a.ReceivedBytes},{a.SentBytes}");
        foreach (var d in history.Daily)
            text.AppendLine($"Laptop,{d.Date},,,{d.ReceivedBytes},{d.SentBytes}");
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.ToString())).ToArray(), "text/csv", $"flow-{history.Range}-{DateTime.Now:yyyy-MM-dd}.csv");
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (Microsoft.Data.Sqlite.SqliteException) { return Results.Json(new { message = "Local history is temporarily unavailable. Try the export again." }, statusCode: 503); }
});
app.MapGet("/api/diagnostics/quality", (NetworkDiagnostics diag) => diag.Quality);
app.MapGet("/api/diagnostics/ping", async (string? host, int? count, NetworkDiagnostics diag, CancellationToken token) =>
{
    try { return Results.Ok(await diag.PingAsync(host ?? "", count ?? 4, token)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapGet("/api/diagnostics/dns", async (string? name, NetworkDiagnostics diag, CancellationToken token) =>
{
    try { return Results.Ok(await diag.DnsBenchmarkAsync(name ?? "google.com", token)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapPost("/api/diagnostics/speedtest", async (NetworkDiagnostics diag, CancellationToken token) =>
{
    return Results.Ok(await diag.SpeedTestAsync(token));
});
app.MapGet("/api/diagnostics/alerts", (NetworkDiagnostics diag) => diag.RecentAlerts());
app.MapPost("/api/diagnostics/alerts/clear", (NetworkDiagnostics diag) => { diag.ClearAlerts(); return Results.Ok(new { success = true }); });
app.MapPost("/api/settings", (Settings settings, TrafficStore store) =>
{
    try { store.SetSettings(settings); return Results.Ok(new { success = true, message = "Budget saved." }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});
app.MapPost("/api/router/connect", async (RouterLogin credentials, RouterClient router, CancellationToken token) =>
{
    var result = await router.Connect(credentials, token);
    return Results.Ok(new { success = result.Success, message = result.Message });
});
app.MapPost("/api/router/disconnect", async (RouterClient router, CancellationToken token) =>
{
    await router.Disconnect(token);
    return Results.Ok(new { success = true, message = router.State.Message });
});
app.MapGet("/api/controls", (ControlService controls, CancellationToken token) => controls.Snapshot(token));
app.MapGet("/api/startup", async (StartupService startup, CancellationToken token) =>
{
    try { return Results.Ok(await startup.Run(null, token)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapPost("/api/startup", async (StartupRequest request, StartupService startup, CancellationToken token) =>
{
    try { return Results.Ok(await startup.Run(request.Enabled, token)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { message = ex.Message }); }
});
app.MapGet("/api/controls/apps/{id}/processes", (string id, ControlService controls) =>
{
    try { return Results.Ok(controls.Processes(id)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
});
async Task<IResult> ControlAction(Func<Task> action)
{
    try { await action(); return Results.Ok(new { success = true }); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or Microsoft.Data.Sqlite.SqliteException)
    { return Results.BadRequest(new { success = false, message = ex.Message }); }
}
app.MapPost("/api/controls/rules", (RuleRequest request, ControlService controls, CancellationToken token) => ControlAction(() => controls.Save(request, token)));
app.MapPost("/api/controls/rules/{id}/reset", (string id, ControlService controls, CancellationToken token) => ControlAction(() => controls.Reset(id, token)));
app.MapPost("/api/controls/rules/{id}/delete", (string id, ControlService controls, CancellationToken token) => ControlAction(() => controls.Remove(id, token)));
app.MapPost("/api/controls/restore", (ControlService controls, CancellationToken token) => ControlAction(() => controls.Restore(token)));
app.MapPost("/api/controls/daily", (DailyBudgetRequest request, ControlService controls) => ControlAction(() => { controls.Daily(request, false); return Task.CompletedTask; }));
app.MapPost("/api/controls/daily/reset", (ControlService controls) => ControlAction(() => { controls.Daily(new(0), true); return Task.CompletedTask; }));
app.MapPost("/api/controls/apps/{id}/end", (string id, EndProcessRequest request, ControlService controls) => ControlAction(() => { controls.End(id, request); return Task.CompletedTask; }));
await app.RunAsync();
