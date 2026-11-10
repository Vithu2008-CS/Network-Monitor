using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Flow;

public sealed class MonitorService(TrafficStore store, RouterClient router, NetworkDiagnostics diagnostics, ILogger<MonitorService> logger) : BackgroundService
{
    private sealed class AppCounter(string id, string name, string path)
    {
        public readonly string Id = id, Name = name, Path = path;
        public long Received, Sent;
        public double Download, Upload;
        public HashSet<string> Protocols = [];
        public DateTimeOffset LastSeen;
    }
    private sealed class Pending
    {
        public long Received, Sent;
        public readonly HashSet<string> Protocols = [];
    }
    private sealed record Identity(string Id, string Name, string Path);
    private readonly object gate = new();
    private readonly Dictionary<ProcessLifetime, Pending> pending = [];
    private readonly ProcessLedger lifetimes = new();
    private readonly Dictionary<string, AppCounter> apps = [];
    private readonly Dictionary<string, (long Received, long Sent)> adapterPrevious = [];
    private readonly Dictionary<int, Identity> identities = [];
    private readonly Queue<Point> points = [];
    private ConnectionRow[] connections = [];
    private AdapterRow[] interfaces = [];
    private readonly DateTimeOffset startedAt = DateTimeOffset.Now;
    private CollectorState collector = new("starting", "Starting Windows network capture…", 0);
    private string? connectionError;
    private TraceEventSession? trace;
    private long received, sent;
    private double download, upload;
    private readonly bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private Dictionary<string, string> wifi = [];

    public AppRow? FindApp(string id)
    {
        lock (gate)
            return apps.TryGetValue(id, out var a) ? new(a.Id, a.Name, a.Path,
                identities.Where(p => p.Value.Id == id).Select(p => p.Key).ToArray(), a.Received, a.Sent,
                a.Download, a.Upload, 0, a.Protocols.ToArray(), a.LastSeen) : null;
    }

    public object Snapshot(AccessInfo? access = null)
    {
        lock (gate)
        {
            var today = store.Totals("day");
            var month = store.Totals("month");
            var appRows = apps.Values.Select(a => new AppRow(a.Id, a.Name, a.Path,
                identities.Where(p => p.Value.Id == a.Id).Select(p => p.Key).ToArray(), a.Received, a.Sent, a.Download, a.Upload,
                connections.Count(c => identities.TryGetValue(c.Pid, out var identity) && identity.Id == a.Id),
                a.Protocols.Order().ToArray(), a.LastSeen)).OrderByDescending(a => a.ReceivedBytes + a.SentBytes).ToArray();
            return new
            {
                machineName = Environment.MachineName, startedAt, now = DateTimeOffset.Now, isAdministrator = elevated,
                collector, summary = new
                {
                    downloadBps = download, uploadBps = upload, receivedBytes = received, sentBytes = sent,
                    todayReceived = today.ReceivedBytes, todaySent = today.SentBytes,
                    monthReceived = month.ReceivedBytes, monthSent = month.SentBytes,
                    activeApps = appRows.Count(a => a.DownloadBps > 0 || a.UploadBps > 0), connectionCount = connections.Length
                },
                series = points.ToArray(), apps = appRows, interfaces, connections, router = router.State,
                settings = store.GetSettings(), access, diagnostics = new {
                    historyError = store.LastError,
                    connectionError,
                    networkQuality = diagnostics.Quality
                },
                alerts = diagnostics.RecentAlerts()
            };
        }
    }

    private void Add(int pid, int size, bool outbound, string protocol, IPAddress source, IPAddress destination, long qpc)
    {
        if (size <= 0 || pid < 0 || Accounting.IsLoopback(source) || Accounting.IsLoopback(destination)) return;
        lock (gate)
        {
            var lifetime = lifetimes.Get(pid, qpc);
            if (!pending.TryGetValue(lifetime, out var value)) pending[lifetime] = value = new();
            if (outbound) value.Sent += size; else value.Received += size;
            value.Protocols.Add(protocol);
        }
    }

    private void Capture(CancellationToken stoppingToken)
    {
        if (!elevated)
        {
            lock (gate) collector = new("needs-admin", "Launch Start Monitor.cmd and approve Windows administrator access to measure usage by program. Adapter totals remain available.", 0);
            return;
        }
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var path = "";
                        try { path = process.MainModule?.FileName ?? ""; } catch (Exception) { }
                        lock (gate) lifetimes.Start(process.Id, process.ProcessName + ".exe", path);
                    }
                    catch (InvalidOperationException) { }
                }
            }
            using var session = new TraceEventSession($"Flow.Network.{Environment.ProcessId}", TraceEventSessionOptions.Create);
            trace = session;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);
            var k = session.Source.Kernel;
            void StartProcess(int pid, string image, long qpc, bool rundown = false)
            {
                var name = Path.GetFileName(image);
                if (string.IsNullOrWhiteSpace(name)) name = $"Process {pid}";
                var path = Path.IsPathFullyQualified(image) ? image : "";
                lock (gate) lifetimes.Start(pid, name, path, qpc, rundown);
            }
            // QPC is intentional: event lifetimes and Stopwatch pruning share the same monotonic Windows clock.
#pragma warning disable CS0618
            k.ProcessStart += e => StartProcess(e.ProcessID, e.ImageFileName, e.TimeStampQPC);
            k.ProcessDCStart += e => StartProcess(e.ProcessID, e.ImageFileName, 0, true);
            k.ProcessStop += e => { lock (gate) lifetimes.Stop(e.ProcessID, e.TimeStampQPC); };
            k.ImageLoad += e =>
            {
                // Main-image paths keep short-lived programs in the correct executable quota; DLLs are ignored.
                if (e.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    lock (gate) lifetimes.Image(e.ProcessID, e.FileName, e.TimeStampQPC);
            };
            k.TcpIpSend += e => Add(e.ProcessID, e.size, true, "TCP", e.saddr, e.daddr, e.TimeStampQPC);
            k.TcpIpRecv += e => Add(e.ProcessID, e.size, false, "TCP", e.saddr, e.daddr, e.TimeStampQPC);
            k.TcpIpSendIPV6 += e => Add(e.ProcessID, e.size, true, "TCP", e.saddr, e.daddr, e.TimeStampQPC);
            k.TcpIpRecvIPV6 += e => Add(e.ProcessID, e.size, false, "TCP", e.saddr, e.daddr, e.TimeStampQPC);
            k.UdpIpSend += e => Add(e.ProcessID, e.size, true, "UDP", e.saddr, e.daddr, e.TimeStampQPC);
            k.UdpIpRecv += e => Add(e.ProcessID, e.size, false, "UDP", e.saddr, e.daddr, e.TimeStampQPC);
            k.UdpIpSendIPV6 += e => Add(e.ProcessID, e.size, true, "UDP", e.saddr, e.daddr, e.TimeStampQPC);
            k.UdpIpRecvIPV6 += e => Add(e.ProcessID, e.size, false, "UDP", e.saddr, e.daddr, e.TimeStampQPC);
#pragma warning restore CS0618
            using var registration = stoppingToken.Register(() => { try { session.Stop(); } catch (Exception) { /* Session may already be stopped. */ } });
            lock (gate) collector = new("running", "Measuring TCP and UDP traffic. Local loopback is excluded.", 0);
            if (!stoppingToken.IsCancellationRequested) session.Source.Process();
            if (!stoppingToken.IsCancellationRequested)
                lock (gate) collector = new("error", "Windows stopped the capture session. Restart the monitor to resume per-program accounting.", session.EventsLost);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Network capture unavailable: {Error}", ex.Message);
            lock (gate) collector = new("error", "Windows network capture could not start. Close other kernel tracing tools and relaunch Start Monitor.cmd as administrator.", 0);
        }
        finally { trace = null; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var capture = Task.Factory.StartNew(() => Capture(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var nextWifi = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.Now >= nextWifi)
                {
                    wifi = await ReadWifi(stoppingToken);
                    nextWifi = DateTime.Now.AddSeconds(20);
                }
                var now = DateTimeOffset.Now;
                var elapsed = Math.Max(0.01, clock.Elapsed.TotalSeconds - last);
                last = clock.Elapsed.TotalSeconds;
                var processes = Process.GetProcesses();
                var existing = processes.ToDictionary(p => p.Id);
                List<ConnectionRow> rawConnections;
                try { rawConnections = NativeConnections.Read(); connectionError = null; }
                catch (Exception ex) { rawConnections = []; connectionError = "Windows connection tables are unavailable: " + ex.Message; }
                var rows = new List<PersistRow>();
                lock (gate)
                {
                    // Re-resolve identities each sample so reused process IDs do not retain the previous program.
                    identities.Clear();
                    Identity Resolve(int pid)
                    {
                        if (identities.TryGetValue(pid, out var found)) return found;
                        var name = pid == 4 ? "System" : $"Process {pid} (exited or protected)";
                        var path = "";
                        if (existing.TryGetValue(pid, out var process))
                        {
                            try { name = process.ProcessName + ".exe"; } catch (Exception) { }
                            try { path = process.MainModule?.FileName ?? ""; } catch (Exception) { }
                        }
                        // shortcut: protected/exited processes lack paths; group those by visible name until lifecycle tracing is needed.
                        var identity = new Identity(Accounting.AppId(string.IsNullOrEmpty(path) ? name : path), name, path);
                        identities[pid] = identity;
                        return identity;
                    }
                    foreach (var app in apps.Values) { app.Download = 0; app.Upload = 0; }
                    AppCounter CounterIdentity(Identity who)
                    {
                        if (!apps.TryGetValue(who.Id, out var counter)) apps[who.Id] = counter = new(who.Id, who.Name, who.Path);
                        return counter;
                    }
                    foreach (var (lifetime, delta) in pending)
                    {
                        var who = lifetimes.IsCurrent(lifetime) && existing.ContainsKey(lifetime.Pid)
                            ? Resolve(lifetime.Pid)
                            : new Identity(Accounting.AppId(lifetime.Path.Length > 0 ? lifetime.Path : lifetime.Name), lifetime.Name, lifetime.Path);
                        var app = CounterIdentity(who);
                        app.Received += delta.Received; app.Sent += delta.Sent;
                        app.Download += delta.Received / elapsed; app.Upload += delta.Sent / elapsed;
                        app.Protocols.UnionWith(delta.Protocols); app.LastSeen = now;
                        rows.Add(new("app", app.Id, app.Name, app.Path, delta.Received, delta.Sent));
                    }
                    pending.Clear();
                    lifetimes.Prune(Stopwatch.GetTimestamp() - 30 * Stopwatch.Frequency);
                    connections = rawConnections.Select(c =>
                    {
                        var app = CounterIdentity(Resolve(c.Pid));
                        app.LastSeen = now;
                        app.Protocols.Add(c.Protocol);
                        return c with { App = app.Name };
                    }).ToArray();
                    interfaces = ReadAdapters(elapsed, rows);
                    var physical = interfaces.Where(a => IsPhysical(a.Type)).ToArray();
                    download = physical.Sum(a => a.DownloadBps); upload = physical.Sum(a => a.UploadBps);
                    received += rows.Where(r => r.Kind == "adapter").Sum(r => r.Received);
                    sent += rows.Where(r => r.Kind == "adapter").Sum(r => r.Sent);
                    points.Enqueue(new(now, download, upload));
                    while (points.Count > 180) points.Dequeue();
                    if (trace is not null)
                    {
                        try { collector = collector with { EventsLost = trace.EventsLost }; } catch (ObjectDisposedException) { }
                    }
                }
                foreach (var process in processes) process.Dispose();
                if (!store.Save(now, rows)) logger.LogWarning("{Error}", store.LastError);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { trace?.Stop(); } catch (Exception) { }
            await capture;
        }
    }

    public static bool IsPhysical(string type) => type is "Wi-Fi" or "Ethernet" or "Mobile broadband";

    private AdapterRow[] ReadAdapters(double elapsed, List<PersistRow> rows)
    {
        var result = new List<AdapterRow>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var props = adapter.GetIPProperties();
                var index = adapter.Supports(NetworkInterfaceComponent.IPv4) ? props.GetIPv4Properties()?.Index : props.GetIPv6Properties()?.Index;
                var flags = index is null ? null : NativeAdapters.Flags(index.Value);
                if (flags?.Filter == true) continue;
                var stats = adapter.GetIPStatistics();
                var current = (Received: stats.BytesReceived, Sent: stats.BytesSent);
                var dr = 0L; var ds = 0L;
                if (adapterPrevious.TryGetValue(adapter.Id, out var previous))
                {
                    dr = Accounting.Delta(current.Received, previous.Received);
                    ds = Accounting.Delta(current.Sent, previous.Sent);
                }
                adapterPrevious[adapter.Id] = current;
                var description = adapter.Description.ToLowerInvariant();
                var type = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                    : adapter.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                      || Regex.IsMatch(description, "virtual|hyper-v|vpn|tap-|wireguard|loopback") ? "VPN / virtual"
                    : adapter.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 ? "Mobile broadband"
                    : adapter.NetworkInterfaceType.ToString().Contains("Ethernet") ? "Ethernet" : adapter.NetworkInterfaceType.ToString();
                if (flags?.Hardware != true && IsPhysical(type)) type = "VPN / virtual";
                if (flags?.Hardware != true && props.UnicastAddresses.Count == 0 && adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (IsPhysical(type)) rows.Add(new("adapter", adapter.Id, adapter.Name, "", dr, ds));
                var gateways = props.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToArray();
                var isWifi = type == "Wi-Fi";
                string? Wifi(string key) => isWifi ? wifi.GetValueOrDefault(key) : null;
                result.Add(new(adapter.Id, adapter.Name, adapter.Description, type, adapter.OperationalStatus.ToString(),
                    props.UnicastAddresses.Select(a => a.Address.ToString()).ToArray(), string.Join(", ", gateways.Select(a => a.ToString())),
                    adapter.GetPhysicalAddress().ToString(), Math.Max(0, adapter.Speed), current.Received, current.Sent, dr / elapsed, ds / elapsed,
                    adapter.OperationalStatus == OperationalStatus.Up && gateways.Length > 0, Wifi("SSID"), Wifi("Signal"), Wifi("Receive rate (Mbps)"), Wifi("Transmit rate (Mbps)")));
            }
            catch (NetworkInformationException) { /* An adapter can disappear while being sampled. */ }
        }
        return result.OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.Status == "Up").ToArray();
    }

    private static async Task<Dictionary<string, string>> ReadWifi(CancellationToken token)
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                // shortcut: netsh labels are localized; untranslated fields fall back to adapter details on non-English Windows.
                foreach (var line in output.Split('\n'))
                {
                    var split = line.IndexOf(':');
                    if (split > 0) result[line[..split].Trim()] = line[(split + 1)..].Trim();
                }
            }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
        }
        catch (System.ComponentModel.Win32Exception) { }
        return result;
    }
}
