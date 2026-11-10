using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Flow;

public sealed class RouterClient : BackgroundService
{
    private const string Host = "http://192.168.8.1";
    private const string LoginToken = "3830c61a-620d-47da-ae47-33d8401401c4", Token = "f3b70f2f-8721-48c4-87ec-22d8c92dd3c9";
    private const string Login = "d2aa9843-494b-4947-9621-a46ec652ecd9", Logout = "677d89ca-2e5c-4481-81e3-cb6965ae77da";
    private const string Devices = "5332f5ee-5be9-4843-b85f-1b251aa5f4ff", Traffic = "24959b3c-291a-47ff-83e6-bcce57de99a3";
    private const string Info = "ece6b6d4-61c7-4dad-af23-c8249c75c58c", Loop = "2ee26212-96cc-45d3-8f0d-808e4cde884a";
    private static readonly HashSet<string> Reads = [LoginToken, Token, Devices, Traffic, Info, Loop];
    private readonly HttpClient http;
    public RouterClient() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    internal RouterClient(HttpClient client) { http = client; http.Timeout = TimeSpan.FromSeconds(8); }
    private readonly SemaphoreSlim gate = new(1, 1);
    private string session = "";
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private volatile RouterState state = new("disconnected", "Connect your router account to view whole-network totals and connected devices. Your password is never saved.");
    public RouterState State => state;

    public static JsonObject ParseReply(string text)
    {
        var start = text.IndexOf('{');
        return start >= 0 && JsonNode.Parse(text[start..]) is JsonObject obj ? obj : throw new InvalidDataException("Invalid router response.");
    }
    private static string Text(JsonObject obj, string key) => obj[key]?.ToString() ?? "";
    private static double Number(JsonObject obj, string key) => double.TryParse(Text(obj, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private async Task<JsonObject> Call(string command, string method = "GET", JsonObject? extra = null, CancellationToken cancellationToken = default)
    {
        if (method == "GET" ? !Reads.Contains(command) : command != Login && command != Logout)
            throw new InvalidOperationException("Router command is not allowed.");
        var data = extra ?? new JsonObject();
        data["cmd"] = command; data["method"] = method; data["sessionId"] = session;
        using var request = new HttpRequestMessage(HttpMethod.Post, Host + "/cgi-bin/http.cgi");
        request.Headers.Add("Origin", Host);
        request.Headers.Referrer = new Uri(Host + "/");
        request.Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var reply = ParseReply(await response.Content.ReadAsStringAsync(cancellationToken));
        if (Text(reply, "message") == "NO_AUTH") throw new InvalidOperationException("The router session expired. Connect again to continue.");
        return reply;
    }
    private async Task<JsonObject> Post(string command, JsonObject? extra = null, CancellationToken cancellationToken = default)
    {
        var token = await Call(Token, cancellationToken: cancellationToken);
        extra ??= new(); extra["token"] = Text(token, "token");
        return await Call(command, "POST", extra, cancellationToken);
    }

    public async Task<(bool Success, string Message)> Connect(RouterLogin credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || credentials.Username.Length > 64 || string.IsNullOrEmpty(credentials.Password) || credentials.Password.Length > 256)
            return (false, "Enter a username and password.");
        await gate.WaitAsync(cancellationToken);
        var authenticated = false;
        try
        {
            if (DateTimeOffset.UtcNow - lastAttempt < TimeSpan.FromSeconds(10)) return (false, "Wait 10 seconds before another login attempt.");
            lastAttempt = DateTimeOffset.UtcNow;
            if (session.Length > 0) await DisconnectCore(cancellationToken);
            var challenge = Text(await Call(LoginToken, cancellationToken: cancellationToken), "token");
            if (challenge.Length == 0) throw new InvalidDataException("The router did not return a login challenge.");
            session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(challenge + credentials.Password))).ToLowerInvariant();
            // The write-token endpoint requires authentication. Login uses the challenge hash and no write token.
            var reply = await Call(Login, "POST", new() { ["username"] = credentials.Username, ["passwd"] = hash, ["token"] = "" }, cancellationToken);
            if (Text(reply, "sessionId").Length == 0 || Text(reply, "login_fail") == "fail" || Text(reply, "login_fail2") == "fail")
            {
                session = "";
                var message = Text(reply, "message") == "alreadyLogin"
                    ? "The router has another active login. Log out of its admin page and try again."
                    : "Router login failed. Check the credentials before retrying; the router may lock repeated failed attempts.";
                state = new("error", message);
                return (false, message);
            }
            session = Text(reply, "sessionId");
            authenticated = true;
            await Refresh(cancellationToken);
            return (true, "Router connected. Only status information is read.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (!authenticated) session = "";
            // If authentication succeeded but a status read failed, keep the session for a later read or logout.
            state = new("error", ex is InvalidOperationException ? ex.Message : "Could not read the router at 192.168.8.1. Check your Wi-Fi connection and try again.");
            return (false, state.Message);
        }
        finally { gate.Release(); }
    }

    private async Task Refresh(CancellationToken token)
    {
        var devices = await Call(Devices, cancellationToken: token);
        var traffic = await Call(Traffic, cancellationToken: token);
        var info = await Call(Info, cancellationToken: token);
        var loop = await Call(Loop, cancellationToken: token);
        var rows = new List<RouterDevice>();
        foreach (var raw in devices["dhcp_list_info"] as JsonArray ?? [])
        {
            if (raw is not JsonObject d) continue;
            JsonObject? wifi = null;
            var type = Text(d, "interface") == "eth" ? "Ethernet" : "Wi-Fi";
            foreach (var band in new[] { ("wlan24g_wifi_info", "2.4 GHz Wi-Fi"), ("wlan5g_wifi_info", "5 GHz Wi-Fi"), ("wlan6g_wifi_info", "6 GHz Wi-Fi") })
            {
                if (devices[band.Item1] is not JsonArray list) continue;
                var found = list.OfType<JsonObject>().FirstOrDefault(w => Text(w, "mac").Equals(Text(d, "mac"), StringComparison.OrdinalIgnoreCase));
                if (found is not null) { wifi = found; type = band.Item2; break; }
            }
            var name = Text(d, "hostname");
            rows.Add(new(name is "" or "*" ? "Unnamed device" : name, Text(d, "ip"), Text(d, "mac"), type,
                wifi is null ? "" : Text(wifi, "rssi"), wifi is null ? "" : Text(wifi, "txrate"), wifi is null ? "" : Text(wifi, "rxrate")));
        }
        var restricted = new[] { devices, traffic, info, loop }.Any(r => Text(r, "message") == "LIMITED_ACCESS");
        state = new("connected", restricted ? "Some status fields are restricted by the router account." : "Read-only router session. Device link speeds are not usage rates.",
            Text(info, "board_type"), Text(info, "fake_version"), Text(loop, "network_type_str"), Text(info, "device_temperature"),
            Number(traffic, "dl_mon_flow"), Number(traffic, "ul_mon_flow"), rows.ToArray(), DateTimeOffset.Now);
    }

    private async Task DisconnectCore(CancellationToken token)
    {
        if (session.Length > 0)
        {
            try
            {
                var reply = await Post(Logout, cancellationToken: token);
                if (Text(reply, "success") != "true") throw new InvalidDataException("The router did not confirm logout.");
            }
            catch (InvalidOperationException) { /* An expired session is already logged out. */ }
        }
        session = "";
        state = new("disconnected", "Router disconnected. Connect again when you need whole-network information.");
    }
    public async Task Disconnect(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await DisconnectCore(token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        { state = state with { Status = "error", Message = "The router could not confirm logout. Its session is retained; retry Disconnect when the router is reachable." }; }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                await gate.WaitAsync(stoppingToken);
                try
                {
                    if (session.Length > 0) await Refresh(stoppingToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
                {
                    if (ex is InvalidOperationException) session = "";
                    state = state with { Status = "error", Message = ex is InvalidOperationException ? ex.Message : "Router unreachable. Last successful readings are shown; reconnecting automatically." };
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Disconnect(timeout.Token);
        }
    }
    public override void Dispose() { http.Dispose(); gate.Dispose(); base.Dispose(); }
}
