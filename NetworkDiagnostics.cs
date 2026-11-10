using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Flow;

public sealed record PingPoint(DateTimeOffset Time, double? GatewayMs, double? InternetMs);

public sealed record NetworkQuality(
    double? GatewayPingMs,
    double? InternetPingMs,
    double? DnsLookupMs,
    double? JitterMs,
    double PacketLossPercent,
    string Quality,
    string GatewayAddress,
    PingPoint[] History);

public sealed record PingResult(
    string Host,
    string Ip,
    int Sent,
    int Received,
    double LossPercent,
    double MinMs,
    double AvgMs,
    double MaxMs,
    string[] Logs);

public sealed record DnsServerTest(
    string Server,
    double ElapsedMs,
    string[] Addresses,
    bool Success,
    string? Error = null);

public sealed record DnsBenchmarkResult(
    string Hostname,
    DnsServerTest[] Tests);

public sealed record SpeedTestResult(
    bool Success,
    double DownloadMbps,
    double DurationSeconds,
    long BytesReceived,
    double PingMs,
    string Server,
    string? Error = null);

public sealed record NetworkAlert(
    string Id,
    DateTimeOffset Time,
    string Level,
    string Title,
    string Detail);

public sealed class NetworkDiagnostics : BackgroundService
{
    private readonly object gate = new();
    private readonly Queue<PingPoint> history = [];
    private readonly List<bool> internetPings = [];
    private readonly List<NetworkAlert> alerts = [];
    private double? lastInternetMs;
    private double? currentGatewayMs;
    private double? currentInternetMs;
    private double? currentDnsMs;
    private double? currentJitterMs;
    private string gatewayIp = "";
    private string quality = "Starting";
    private readonly HttpClient http;

    public NetworkDiagnostics()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Flow-Network-Monitor/2.0");
    }

    public NetworkQuality Quality
    {
        get
        {
            lock (gate)
            {
                var loss = internetPings.Count > 0
                    ? Math.Round((double)internetPings.Count(p => !p) / internetPings.Count * 100, 1)
                    : 0;
                return new NetworkQuality(
                    currentGatewayMs,
                    currentInternetMs,
                    currentDnsMs,
                    currentJitterMs,
                    loss,
                    quality,
                    gatewayIp,
                    history.ToArray());
            }
        }
    }

    public NetworkAlert[] RecentAlerts(int max = 15)
    {
        lock (gate)
        {
            return alerts.OrderByDescending(a => a.Time).Take(max).ToArray();
        }
    }

    public void AddAlert(string level, string title, string detail)
    {
        lock (gate)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            alerts.Add(new NetworkAlert(id, DateTimeOffset.Now, level, title, detail));
            while (alerts.Count > 50) alerts.RemoveAt(0);
        }
    }

    public static string FindDefaultGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var gw in props.GatewayAddresses)
                {
                    var addr = gw.Address;
                    if (!addr.Equals(IPAddress.Any) && !addr.Equals(IPAddress.IPv6Any) &&
                        addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr.ToString();
                    }
                }
            }
        }
        catch { }
        return "";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dnsCounter = 0;
        using var pingSender = new Ping();

        while (!stoppingToken.IsCancellationRequested)
        {
            var gw = FindDefaultGateway();
            double? gwMs = null;
            double? netMs = null;

            if (!string.IsNullOrEmpty(gw))
            {
                try
                {
                    var reply = await pingSender.SendPingAsync(gw, 1000);
                    if (reply.Status == IPStatus.Success) gwMs = reply.RoundtripTime;
                }
                catch { }
            }

            try
            {
                var netReply = await pingSender.SendPingAsync("1.1.1.1", 1200);
                if (netReply.Status == IPStatus.Success) netMs = netReply.RoundtripTime;
                else
                {
                    // Fallback test
                    var fallback = await pingSender.SendPingAsync("8.8.8.8", 1200);
                    if (fallback.Status == IPStatus.Success) netMs = fallback.RoundtripTime;
                }
            }
            catch { }

            // Periodic lightweight DNS resolution check (every ~15s)
            double? dnsMs = null;
            if (++dnsCounter % 5 == 0)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ips = await Dns.GetHostAddressesAsync("one.one.one.one", stoppingToken);
                    sw.Stop();
                    if (ips.Length > 0) dnsMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                }
                catch { }
            }

            lock (gate)
            {
                gatewayIp = gw;
                currentGatewayMs = gwMs;
                currentInternetMs = netMs;
                if (dnsMs.HasValue) currentDnsMs = dnsMs;

                if (netMs.HasValue && lastInternetMs.HasValue)
                {
                    var j = Math.Abs(netMs.Value - lastInternetMs.Value);
                    currentJitterMs = currentJitterMs.HasValue
                        ? Math.Round(currentJitterMs.Value * 0.8 + j * 0.2, 1)
                        : Math.Round(j, 1);
                }
                if (netMs.HasValue) lastInternetMs = netMs.Value;

                internetPings.Add(netMs.HasValue);
                while (internetPings.Count > 25) internetPings.RemoveAt(0);

                var loss = (double)internetPings.Count(p => !p) / internetPings.Count * 100;
                var prevQuality = quality;

                if (string.IsNullOrEmpty(gw) || (internetPings.Count >= 5 && loss >= 90))
                    quality = "Offline";
                else if (loss >= 15 || (netMs.HasValue && netMs.Value > 250))
                    quality = "Degraded";
                else if (loss > 0 || (netMs.HasValue && netMs.Value > 100))
                    quality = "Fair";
                else if (netMs.HasValue && netMs.Value <= 50 && (!currentJitterMs.HasValue || currentJitterMs.Value <= 15))
                    quality = "Optimal";
                else
                    quality = "Good";

                if (prevQuality != "Starting" && prevQuality != quality)
                {
                    if (quality == "Offline")
                        AddAlertInternal("error", "Internet Disconnected", "Internet gateway or DNS is unreachable.");
                    else if (quality == "Degraded")
                        AddAlertInternal("warning", "Connection Degraded", $"High latency ({netMs} ms) or packet loss detected.");
                    else if (prevQuality is "Offline" or "Degraded" && quality is "Optimal" or "Good")
                        AddAlertInternal("info", "Connection Restored", $"Network health returned to {quality} ({netMs} ms).");
                }

                history.Enqueue(new PingPoint(DateTimeOffset.Now, gwMs, netMs));
                while (history.Count > 30) history.Dequeue();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void AddAlertInternal(string level, string title, string detail)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        alerts.Add(new NetworkAlert(id, DateTimeOffset.Now, level, title, detail));
        while (alerts.Count > 50) alerts.RemoveAt(0);
    }

    public async Task<PingResult> PingAsync(string host, int count = 4, CancellationToken token = default)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Please enter a host or IP address to ping.");

        count = Math.Clamp(count, 1, 10);
        var logs = new List<string>();
        using var pinger = new Ping();

        IPAddress? targetIp = null;
        if (!IPAddress.TryParse(host, out targetIp))
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, token);
                targetIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Could not resolve host '{host}': {ex.Message}");
            }
        }

        if (targetIp == null)
            throw new ArgumentException($"No valid IP found for host '{host}'.");

        var resolvedIp = targetIp.ToString();
        logs.Add($"Pinging {host} [{resolvedIp}] with 32 bytes of data:");

        var rtts = new List<long>();
        var sent = 0;
        var received = 0;

        for (var i = 0; i < count; i++)
        {
            if (token.IsCancellationRequested) break;
            sent++;
            try
            {
                var reply = await pinger.SendPingAsync(targetIp, 2000, new byte[32]);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    rtts.Add(reply.RoundtripTime);
                    logs.Add($"Reply from {resolvedIp}: bytes=32 time={reply.RoundtripTime}ms TTL={reply.Options?.Ttl ?? 64}");
                }
                else
                {
                    logs.Add($"Request timed out ({reply.Status}).");
                }
            }
            catch (Exception ex)
            {
                logs.Add($"Ping error: {ex.Message}");
            }

            if (i < count - 1) await Task.Delay(500, token);
        }

        var loss = sent > 0 ? Math.Round((double)(sent - received) / sent * 100, 1) : 100;
        var min = rtts.Count > 0 ? (double)rtts.Min() : 0;
        var max = rtts.Count > 0 ? (double)rtts.Max() : 0;
        var avg = rtts.Count > 0 ? Math.Round(rtts.Average(), 1) : 0;

        logs.Add($"Ping statistics: Sent = {sent}, Received = {received}, Lost = {sent - received} ({loss}% loss)");
        if (rtts.Count > 0) logs.Add($"Approximate round trip times: Min = {min}ms, Max = {max}ms, Avg = {avg}ms");

        return new PingResult(host, resolvedIp, sent, received, loss, min, avg, max, logs.ToArray());
    }

    public async Task<DnsBenchmarkResult> DnsBenchmarkAsync(string hostname = "google.com", CancellationToken token = default)
    {
        hostname = string.IsNullOrWhiteSpace(hostname) ? "google.com" : hostname.Trim();
        var tests = new List<DnsServerTest>();

        // 1. System Default DNS
        var sw = Stopwatch.StartNew();
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(hostname, token);
            sw.Stop();
            tests.Add(new DnsServerTest("System Resolver", Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                addrs.Select(a => a.ToString()).ToArray(), true));
        }
        catch (Exception ex)
        {
            sw.Stop();
            tests.Add(new DnsServerTest("System Resolver", Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                [], false, ex.Message));
        }

        // 2. Cloudflare (1.1.1.1)
        tests.Add(await QueryDnsUdpAsync(hostname, "1.1.1.1", "Cloudflare (1.1.1.1)", token));

        // 3. Google (8.8.8.8)
        tests.Add(await QueryDnsUdpAsync(hostname, "8.8.8.8", "Google (8.8.8.8)", token));

        // 4. Quad9 (9.9.9.9)
        tests.Add(await QueryDnsUdpAsync(hostname, "9.9.9.9", "Quad9 (9.9.9.9)", token));

        return new DnsBenchmarkResult(hostname, tests.ToArray());
    }

    private static async Task<DnsServerTest> QueryDnsUdpAsync(string hostname, string serverIp, string serverName, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var query = BuildDnsQuery(hostname);
            using var udp = new UdpClient();
            udp.Client.SendTimeout = 2000;
            udp.Client.ReceiveTimeout = 2000;

            var target = new IPEndPoint(IPAddress.Parse(serverIp), 53);
            await udp.SendAsync(query, query.Length, target);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));

            var result = await udp.ReceiveAsync(cts.Token);
            sw.Stop();
            var ips = ParseDnsResponse(result.Buffer);
            return new DnsServerTest(serverName, Math.Round(sw.Elapsed.TotalMilliseconds, 1), ips, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DnsServerTest(serverName, Math.Round(sw.Elapsed.TotalMilliseconds, 1), [], false, ex.Message);
        }
    }

    private static byte[] BuildDnsQuery(string domain)
    {
        var packet = new List<byte>
        {
            0x12, 0x34, // Transaction ID
            0x01, 0x00, // Flags: Standard query, recursion desired
            0x00, 0x01, // Questions: 1
            0x00, 0x00, // Answer RRs: 0
            0x00, 0x00, // Authority RRs: 0
            0x00, 0x00  // Additional RRs: 0
        };

        foreach (var part in domain.Split('.'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            packet.Add((byte)part.Length);
            packet.AddRange(System.Text.Encoding.ASCII.GetBytes(part));
        }
        packet.Add(0x00); // End of name
        packet.AddRange([0x00, 0x01]); // Type A
        packet.AddRange([0x00, 0x01]); // Class IN
        return packet.ToArray();
    }

    private static string[] ParseDnsResponse(byte[] buffer)
    {
        var ips = new List<string>();
        try
        {
            if (buffer.Length < 12) return [];
            var ancount = (buffer[6] << 8) | buffer[7];
            var offset = 12;

            // Skip question section
            while (offset < buffer.Length && buffer[offset] != 0)
            {
                if ((buffer[offset] & 0xC0) == 0xC0) { offset += 2; break; }
                offset += buffer[offset] + 1;
            }
            if (offset < buffer.Length && buffer[offset] == 0) offset++;
            offset += 4; // Skip Type and Class

            // Read answers
            for (var i = 0; i < ancount && offset < buffer.Length; i++)
            {
                if ((buffer[offset] & 0xC0) == 0xC0) offset += 2;
                else
                {
                    while (offset < buffer.Length && buffer[offset] != 0) offset += buffer[offset] + 1;
                    if (offset < buffer.Length) offset++;
                }
                if (offset + 10 > buffer.Length) break;
                var type = (buffer[offset] << 8) | buffer[offset + 1];
                var rdlength = (buffer[offset + 8] << 8) | buffer[offset + 9];
                offset += 10;
                if (type == 1 && rdlength == 4 && offset + 4 <= buffer.Length) // Type A
                {
                    ips.Add($"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}");
                }
                offset += rdlength;
            }
        }
        catch { }
        return ips.ToArray();
    }

    public async Task<SpeedTestResult> SpeedTestAsync(CancellationToken token = default)
    {
        // Try Cloudflare speed test endpoint, fallback to Microsoft traceevent package
        var testUrls = new[]
        {
            ("Cloudflare CDN", "https://speed.cloudflare.com/__down?bytes=10000000"),
            ("Microsoft Azure CDN", "https://api.nuget.org/v3-flatcontainer/microsoft.diagnostics.tracing.traceevent/3.2.8/microsoft.diagnostics.tracing.traceevent.3.2.8.nupkg")
        };

        foreach (var (serverName, url) in testUrls)
        {
            try
            {
                // Measure initial ping/latency
                var pingSw = Stopwatch.StartNew();
                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                using var headRes = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, token);
                pingSw.Stop();
                var pingMs = Math.Round(pingSw.Elapsed.TotalMilliseconds, 1);

                // Download and measure throughput
                var sw = Stopwatch.StartNew();
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (!res.IsSuccessStatusCode) continue;

                await using var stream = await res.Content.ReadAsStreamAsync(token);
                var buffer = new byte[65536];
                long totalBytes = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    totalBytes += read;
                }
                sw.Stop();

                var seconds = Math.Max(0.01, sw.Elapsed.TotalSeconds);
                var mbps = Math.Round((totalBytes * 8.0) / (seconds * 1_000_000.0), 2);

                return new SpeedTestResult(true, mbps, Math.Round(seconds, 2), totalBytes, pingMs, serverName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try next endpoint
            }
        }

        return new SpeedTestResult(false, 0, 0, 0, 0, "None", "Speed test failed. Check internet connectivity.");
    }
}

