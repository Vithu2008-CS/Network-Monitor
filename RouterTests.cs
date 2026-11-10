using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Flow;

internal static class RouterTests
{
    private sealed class RouterHandler : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        private bool loggedIn;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            var cmd = body["cmd"]!.ToString();
            Calls.Add(cmd);
            JsonObject reply;
            if (cmd == "3830c61a-620d-47da-ae47-33d8401401c4") reply = new() { ["success"] = true, ["token"] = "challenge" };
            else if (cmd == "d2aa9843-494b-4947-9621-a46ec652ecd9")
            {
                var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("challenge" + "test-only-password"))).ToLowerInvariant();
                if (body["passwd"]?.ToString() != expected || body["method"]?.ToString() != "POST" || body["token"]?.ToString() != "")
                    throw new InvalidOperationException("Incorrect login handshake.");
                loggedIn = true;
                reply = new() { ["success"] = true, ["sessionId"] = "test-session" };
            }
            else if (!loggedIn || body["sessionId"]?.ToString() != "test-session") reply = new() { ["success"] = false, ["message"] = "NO_AUTH" };
            else reply = cmd switch
            {
                "5332f5ee-5be9-4843-b85f-1b251aa5f4ff" => new() { ["success"] = true, ["dhcp_list_info"] = new JsonArray(new JsonObject { ["hostname"] = "Test laptop", ["ip"] = "192.168.8.2", ["mac"] = "test", ["interface"] = "eth" }) },
                "24959b3c-291a-47ff-83e6-bcce57de99a3" => new() { ["success"] = true, ["dl_mon_flow"] = "125.5", ["ul_mon_flow"] = "20.25" },
                "ece6b6d4-61c7-4dad-af23-c8249c75c58c" => new() { ["success"] = true, ["board_type"] = "ZLT E200", ["device_temperature"] = "55" },
                "2ee26212-96cc-45d3-8f0d-808e4cde884a" => new() { ["success"] = true, ["network_type_str"] = "5G" },
                "f3b70f2f-8721-48c4-87ec-22d8c92dd3c9" => new() { ["success"] = true, ["token"] = "write-token" },
                "677d89ca-2e5c-4481-81e3-cb6965ae77da" => new() { ["success"] = true },
                _ => throw new InvalidOperationException("Unexpected router command.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString()) };
        }
    }

    public static void Run(Action<bool, string> check)
    {
        using var handler = new RouterHandler();
        using var router = new RouterClient(new HttpClient(handler));
        var result = router.Connect(new("user", "test-only-password"), CancellationToken.None).GetAwaiter().GetResult();
        check(result.Success, "router login works when pre-login write-token endpoint rejects unauthenticated requests");
        check(handler.Calls[1] == "d2aa9843-494b-4947-9621-a46ec652ecd9", "login precedes authenticated write-token reads");
        check(router.State.Devices?.Single().Name == "Test laptop" && router.State.MonthlyDownloadMb == 125.5, "authenticated router status is populated");
        router.Disconnect(CancellationToken.None).GetAwaiter().GetResult();
        check(router.State.Status == "disconnected", "authenticated logout works");
    }
}
