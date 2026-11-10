using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Flow;

public static class NativeConnections
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);

    private static readonly ConcurrentDictionary<string, string> HostCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolveService(int port, string protocol) => port switch
    {
        80 => "HTTP",
        443 => "HTTPS",
        53 => "DNS",
        853 => "DoT",
        22 => "SSH",
        21 => "FTP",
        25 => "SMTP",
        110 => "POP3",
        143 => "IMAP",
        993 => "IMAPS",
        995 => "POP3S",
        587 => "SMTPS",
        3389 => "RDP",
        8080 or 8443 or 8000 => "Web-Alt",
        5353 => "mDNS",
        1900 => "SSDP",
        123 => "NTP",
        67 or 68 => "DHCP",
        5099 => "Flow",
        _ => ""
    };

    public static string? TryGetHost(string ip)
    {
        if (string.IsNullOrEmpty(ip) || ip == "*" || ip == "0.0.0.0" || ip == "::" || ip.StartsWith("127.") || ip == "::1") return null;
        if (HostCache.TryGetValue(ip, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;

        if (InFlight.TryAdd(ip, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
                    var name = entry.HostName;
                    var org = IdentifyOrg(name);
                    HostCache[ip] = string.IsNullOrEmpty(org) ? name : $"{name} ({org})";
                }
                catch
                {
                    HostCache[ip] = "";
                }
                finally
                {
                    InFlight.TryRemove(ip, out _);
                }
            });
        }
        return null;
    }

    public static string IdentifyOrg(string host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var lower = host.ToLowerInvariant();
        if (lower.Contains("1e100.net") || lower.Contains("google") || lower.Contains("youtube")) return "Google";
        if (lower.Contains("microsoft") || lower.Contains("azure") || lower.Contains("live.com") || lower.Contains("office.com")) return "Microsoft";
        if (lower.Contains("amazonaws") || lower.Contains("cloudfront") || lower.Contains("aws")) return "AWS";
        if (lower.Contains("cloudflare") || lower == "one.one.one.one") return "Cloudflare";
        if (lower.Contains("github")) return "GitHub";
        if (lower.Contains("apple.com") || lower.Contains("icloud")) return "Apple";
        if (lower.Contains("fastly")) return "Fastly";
        if (lower.Contains("akamai")) return "Akamai";
        if (lower.Contains("fbcdn") || lower.Contains("facebook") || lower.Contains("meta") || lower.Contains("whatsapp")) return "Meta";
        if (lower.Contains("discord")) return "Discord";
        if (lower.Contains("steam")) return "Steam";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("netflix")) return "Netflix";
        return "";
    }

    public static List<ConnectionRow> Read()
    {
        var result = new List<ConnectionRow>();
        foreach (var ipv6 in new[] { false, true })
        foreach (var tcp in new[] { true, false })
        {
            var family = ipv6 ? 23 : 2;
            uint size = 0;
            uint Query(IntPtr buffer, ref uint length) => tcp
                ? GetExtendedTcpTable(buffer, ref length, true, family, 5, 0)
                : GetExtendedUdpTable(buffer, ref length, true, family, 1, 0);
            Query(IntPtr.Zero, ref size);
            if (size == 0) continue;
            // The table can grow between calls; retry only ERROR_INSUFFICIENT_BUFFER.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    var status = Query(buffer, ref size);
                    if (status == 122) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    var count = Marshal.ReadInt32(buffer);
                    var rowSize = tcp ? (ipv6 ? 56 : 24) : (ipv6 ? 28 : 12);
                    if (count < 0 || (long)count * rowSize + 4 > size) throw new InvalidDataException("Invalid connection table size.");
                    for (var i = 0; i < count; i++)
                    {
                        var row = IntPtr.Add(buffer, 4 + i * rowSize);
                        string Address(int offset, int scopeOffset = -1)
                        {
                            if (!ipv6) return new IPAddress(unchecked((uint)Marshal.ReadInt32(row, offset))).ToString();
                            var bytes = new byte[16];
                            Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, 16);
                            return new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, scopeOffset))).ToString();
                        }
                        int Port(int offset) => (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
                        var local = Address(ipv6 ? 0 : (tcp ? 4 : 0), 16);
                        var localPort = Port(ipv6 ? 20 : (tcp ? 8 : 4));
                        var remote = tcp ? Address(ipv6 ? 24 : 12, 40) : "*";
                        var remotePort = tcp ? Port(ipv6 ? 44 : 16) : 0;
                        var pid = Marshal.ReadInt32(row, tcp ? (ipv6 ? 52 : 20) : (ipv6 ? 24 : 8));
                        var state = tcp ? ((TcpState)Marshal.ReadInt32(row, ipv6 ? 48 : 0)).ToString() : "Bound";
                        var service = ResolveService(remotePort > 0 ? remotePort : localPort, tcp ? "TCP" : "UDP");
                        var remoteHost = tcp && remote != "*" ? TryGetHost(remote) : null;
                        result.Add(new(pid, "", tcp ? "TCP" : "UDP", local, localPort, remote, remotePort, state, remoteHost, service));
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return result;
    }
}
