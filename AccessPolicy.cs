using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Flow;

public sealed record AccessInfo(bool IsRemote, bool MobileEnabled, string? MobileUrl, bool CanEndProcesses = false);

public sealed class AccessPolicy
{
    public IPAddress? LanAddress { get; }
    private readonly string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public AccessPolicy(IPAddress? lanAddress = null) { LanAddress = lanAddress; }
    public string[] ListenUrls => LanAddress is null ? ["http://localhost:5099"] : ["http://localhost:5099", $"http://{LanAddress}:5099"];
    public static bool IsLocal(HttpContext context) => context.Connection.RemoteIpAddress is { } address && Accounting.IsLoopback(address);
    public AccessInfo Info(HttpContext context) => new(!IsLocal(context), LanAddress is not null,
        IsLocal(context) && LanAddress is not null ? $"http://{LanAddress}:5099/#pair={key}" : null, IsLocal(context) || HasKey(context));

    private bool HasKey(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Flow-Key"].ToString();
        return supplied.Length == key.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(key));
    }

    public bool Allows(HttpContext context)
    {
        var host = context.Request.Host;
        if (host.Port != 5099 || !(host.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.Host == LanAddress?.ToString())) return false;
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"http://{host}" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        if (IsLocal(context)) return context.Request.Method is "GET" or "HEAD" || context.Request.Headers["X-Flow-Request"] == "1";
        if (LanAddress is null) return false;
        if (context.Request.Method is not ("GET" or "HEAD"))
            return context.Request.Method == "POST" && context.Request.Headers["X-Flow-Request"] == "1" && HasKey(context) &&
                Regex.IsMatch(context.Request.Path.Value ?? "", "^/api/controls/apps/[0-9A-Fa-f]{20}/end$", RegexOptions.CultureInvariant);
        if (!context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/api/health") return true;
        return HasKey(context);
    }

    public static IPAddress FindLanAddress(string? preferred = null)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
            var props = adapter.GetIPProperties();
            var index = props.GetIPv4Properties()?.Index;
            if (index is null || NativeAdapters.Flags(index.Value) is not { Hardware: true, Filter: false } || props.GatewayAddresses.Count == 0) continue;
            var address = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (address is not null && (preferred is null || address.ToString() == preferred)) return address;
        }
        throw new InvalidOperationException("Mobile sharing needs an active Wi-Fi or Ethernet connection with an IPv4 gateway.");
    }
}
