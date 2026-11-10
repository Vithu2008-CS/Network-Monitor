using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Flow;

public interface IWindowsRules
{
    Task Apply(AppRule rule, bool block, CancellationToken token);
    Task Remove(string appId, CancellationToken token);
    Task Clear(CancellationToken token);
}

public sealed class WindowsRules : IWindowsRules
{
    private const string Prefix = "Flow.App.";
    private static void Firewall(string id, string? path, bool block)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            foreach (var direction in new[] { 1, 2 })
            {
                var name = Prefix + id + "." + direction;
                try { policy.Rules.Remove(name); }
                catch (System.IO.FileNotFoundException) { }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                if (!block) continue;
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                try
                {
                    rule.Name = name; rule.Description = "Managed by Flow. Restore access in Flow or run Restore Access.cmd.";
                    rule.ApplicationName = path!; rule.Direction = direction; rule.Action = 0; rule.Enabled = true;
                    rule.Profiles = int.MaxValue; rule.InterfaceTypes = "All";
                    policy.Rules.Add(rule);
                }
                finally { Marshal.FinalReleaseComObject(rule); }
            }
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

    private const string QosScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
        $d = [Console]::In.ReadToEnd() | ConvertFrom-Json
        if ($d.clear) {
          Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object { $_.Name -like 'Flow.App.*' } | Remove-NetQosPolicy -PolicyStore ActiveStore -Confirm:$false
        } else {
          $p = Get-NetQosPolicy -Name $d.name -PolicyStore ActiveStore -ErrorAction SilentlyContinue
          if ($d.kbps -le 0) {
            if ($p) { Remove-NetQosPolicy -Name $d.name -PolicyStore ActiveStore -Confirm:$false }
          } elseif ($p) {
            Set-NetQosPolicy -Name $d.name -PolicyStore ActiveStore -AppPathNameMatchCondition $d.path -NetworkProfile All -ThrottleRateActionBitsPerSecond ([uint64]($d.kbps * 8000))
          } else {
            New-NetQosPolicy -Name $d.name -PolicyStore ActiveStore -AppPathNameMatchCondition $d.path -NetworkProfile All -ThrottleRateActionBitsPerSecond ([uint64]($d.kbps * 8000)) | Out-Null
          }
        }
        exit 0
        } catch {
          [Console]::Error.WriteLine($_.Exception.Message)
          exit 1
        }
        """;

    private static async Task Qos(object payload, CancellationToken token)
    {
        using var process = new Process { StartInfo = new("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(QosScript)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(payload));
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw new InvalidOperationException("Windows QoS did not respond in time."); }
        await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows QoS could not apply the upload limit: " + (await errors)[..Math.Min(350, (await errors).Length)]);
    }

    private readonly Dictionary<string, NativeRuleState> applied = [];
    public async Task Apply(AppRule rule, bool block, CancellationToken token)
    {
        var limit = rule.Enabled ? rule.UploadLimitKbps : 0;
        applied.TryGetValue(rule.AppId, out var previous);
        if (previous?.Blocked != block) Firewall(rule.AppId, rule.Path, block);
        // Firewall changes are tracked even if the optional QoS part fails, allowing a later unblock to succeed.
        applied[rule.AppId] = new(block, previous?.UploadLimitKbps ?? 0);
        if ((previous?.UploadLimitKbps ?? 0) != limit)
            await Qos(new { name = Prefix + rule.AppId, path = rule.Path, kbps = limit }, token);
        applied[rule.AppId] = new(block, limit);
    }
    public async Task Remove(string appId, CancellationToken token)
    {
        Firewall(appId, null, false);
        await Qos(new { name = Prefix + appId, path = "", kbps = 0 }, token);
        applied.Remove(appId);
    }
    public async Task Clear(CancellationToken token)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        try
        {
            var names = new List<string>();
            foreach (dynamic rule in policy.Rules)
            {
                try { if (((string)rule.Name).StartsWith(Prefix, StringComparison.Ordinal)) names.Add((string)rule.Name); }
                finally { Marshal.FinalReleaseComObject(rule); }
            }
            foreach (var name in names) policy.Rules.Remove(name);
        }
        finally { Marshal.FinalReleaseComObject(policy); }
        await Qos(new { clear = true }, token);
        applied.Clear();
    }
}
