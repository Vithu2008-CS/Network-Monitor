namespace Flow;

// Each start creates a distinct object, so buffered bytes survive PID reuse and process exit.
public sealed class ProcessLifetime(int pid, string name, string path, long startQpc = 0)
{
    public int Pid { get; } = pid;
    public string Name { get; private set; } = name;
    public string Path { get; private set; } = path;
    public long StartQpc { get; } = startQpc;
    public long? StopQpc { get; set; }
    public void SetImage(string image)
    {
        if (!System.IO.Path.IsPathFullyQualified(image)) return;
        Path = image; Name = System.IO.Path.GetFileName(image);
    }
}

public sealed class ProcessLedger
{
    private readonly Dictionary<int, ProcessLifetime> active = [];
    private readonly Dictionary<int, List<ProcessLifetime>> history = [];
    public ProcessLifetime Start(int pid, string name, string path, long qpc = 0, bool rundown = false)
    {
        if (rundown && active.TryGetValue(pid, out var existing)) return existing;
        if (!history.TryGetValue(pid, out var versions)) history[pid] = versions = [];
        if (active.TryGetValue(pid, out var previous) && qpc > 0) previous.StopQpc = qpc - 1;
        var value = new ProcessLifetime(pid, name, path, qpc);
        versions.Add(value);
        return active[pid] = value;
    }
    public ProcessLifetime Get(int pid, long qpc = long.MaxValue)
    {
        if (history.TryGetValue(pid, out var versions))
            for (var i = versions.Count - 1; i >= 0; i--)
                if (versions[i].StartQpc <= qpc && (!versions[i].StopQpc.HasValue || qpc <= versions[i].StopQpc)) return versions[i];
        return new(pid, $"Process {pid} (unresolved)", "");
    }
    public void Stop(int pid, long qpc = long.MaxValue)
    {
        if (active.Remove(pid, out var value)) value.StopQpc = qpc;
    }
    public void Image(int pid, string path, long qpc) => Get(pid, qpc).SetImage(path);
    public void Prune(long beforeQpc)
    {
        foreach (var pid in history.Keys.ToArray())
        {
            history[pid].RemoveAll(v => v.StopQpc.HasValue && v.StopQpc < beforeQpc);
            if (history[pid].Count == 0) history.Remove(pid);
        }
    }
    public bool IsCurrent(ProcessLifetime value) => active.TryGetValue(value.Pid, out var current) && ReferenceEquals(value, current);
}
