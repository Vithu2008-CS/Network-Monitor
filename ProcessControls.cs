using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flow;

public static class ProcessControls
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(IntPtr process, out bool critical);

    public static string? ProtectionReason(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\")) return "A local executable path is required.";
        path = Path.GetFullPath(path);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return "Windows components are protected.";
        if (path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return "The monitor's own executable is protected.";
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "The executable is unavailable.";
        return null;
    }
    public static ProcessTarget[] List(string path)
    {
        var rows = new List<ProcessTarget>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                    var reason = ProtectionReason(path);
                    if (!IsProcessCritical(process.Handle, out var critical) || critical) reason = "Critical or protected process.";
                    rows.Add(new(process.Id, process.ProcessName, process.StartTime.ToUniversalTime().Ticks.ToString(), reason is null, reason ?? "", process.WorkingSet64, process.TotalProcessorTime.TotalSeconds));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
        return rows.ToArray();
    }
    public static void End(string path, EndProcessRequest target)
    {
        if (ProtectionReason(path) is { } reason) throw new ArgumentException(reason);
        if (target.Pid <= 4 || target.Pid == Environment.ProcessId) throw new ArgumentException("This process is protected.");
        using var process = Process.GetProcessById(target.Pid);
        // Validate the handle's lifetime and image immediately before killing; a reused PID is not the same process.
        if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase) || process.StartTime.ToUniversalTime().Ticks.ToString() != target.StartToken)
            throw new ArgumentException("The process changed or exited. Refresh the list before trying again.");
        if (!IsProcessCritical(process.Handle, out var critical) || critical) throw new ArgumentException("Critical or protected process.");
        process.Kill(entireProcessTree: false);
    }
}
