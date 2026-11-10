using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Flow;

public sealed record StartupRequest(bool Enabled);

public sealed class StartupService(IHostEnvironment environment, AccessPolicy access, TrafficStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<JsonObject> Run(bool? enabled, CancellationToken token)
    {
        if (enabled is not null && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ArgumentException("Start Flow as administrator to change automatic startup.");
        await gate.WaitAsync(token);
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = environment.ContentRootPath };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(environment.ContentRootPath, "Start-at-SignIn.ps1"), "-Action", enabled is null ? "Status" : enabled.Value ? "Enable" : "Disable" }) start.ArgumentList.Add(argument);
            if (access.LanAddress is not null) start.ArgumentList.Add("-Share");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not access Windows Task Scheduler.");
            var output = process.StandardOutput.ReadToEndAsync(token); var errors = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw new InvalidOperationException("Windows Task Scheduler did not respond. Refresh startup status before trying again."); }
            var text = await output; var error = await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not configure startup: " + error[..Math.Min(500, error.Length)]);
            if (JsonNode.Parse(text) is not JsonObject result) throw new InvalidOperationException("Task Scheduler returned an invalid response.");
            if (enabled is not null) store.LogAction(enabled.Value ? "Startup enabled" : "Startup disabled", "Flow", "Windows sign-in/unlock task updated for this user. No credentials stored.");
            return result;
        }
        finally { gate.Release(); }
    }
}
