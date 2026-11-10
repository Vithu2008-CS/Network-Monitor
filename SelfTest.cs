using System.Net;

namespace Flow;

public static class SelfTest
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + message);
            count++;
        }
        Check(Accounting.Delta(500, 200) == 300, "counter growth");
        Check(Accounting.Delta(12, 900) == 0, "adapter reset cannot create negative usage");
        Check(Accounting.IsLoopback(IPAddress.Parse("127.0.0.1")), "IPv4 loopback");
        Check(Accounting.IsLoopback(IPAddress.Parse("::1")), "IPv6 loopback");
        Check(Accounting.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")), "mapped loopback");
        Check(!Accounting.IsLoopback(IPAddress.Parse("192.168.8.1")), "LAN traffic retained");
        Check(!Accounting.IsLoopback(IPAddress.Parse("2606:4700:4700::1111")), "external IPv6 retained");
        Check(Accounting.AppId("C:\\Apps\\Test.exe") == Accounting.AppId("c:\\apps\\test.exe"), "Windows path grouping");
        Check(Accounting.SafeCsv("=cmd").StartsWith("\"'="), "CSV formula injection");
        Check(Accounting.Csv("a\"b") == "\"a\"\"b\"", "CSV quoting");
        Check(RouterClient.ParseReply("prefix\n{\"success\":true}")["success"]!.GetValue<bool>(), "router response prefix");
        RouterTests.Run(Check);
        Check(NativeConnections.ResolveService(443, "TCP") == "HTTPS", "service resolution HTTPS");
        Check(NativeConnections.ResolveService(53, "UDP") == "DNS", "service resolution DNS");
        Check(NativeConnections.ResolveService(22, "TCP") == "SSH", "service resolution SSH");
        Check(NativeConnections.IdentifyOrg("server.1e100.net") == "Google", "org identification Google");
        Check(NativeConnections.IdentifyOrg("one.one.one.one") == "Cloudflare", "org identification Cloudflare");
        Check(NativeConnections.IdentifyOrg("ec2.amazonaws.com") == "AWS", "org identification AWS");
        var diag = new NetworkDiagnostics();
        Check(diag.Quality is not null, "diagnostics quality initialization");
        diag.AddAlert("info", "Test Alert", "Testing alert system");
        Check(diag.RecentAlerts().Length >= 1, "alert recording");
        var policy = new AccessPolicy(IPAddress.Parse("192.168.8.138"));
        var localRequest = new DefaultHttpContext();
        localRequest.Connection.RemoteIpAddress = IPAddress.Loopback;
        localRequest.Request.Host = new HostString("localhost", 5099);
        localRequest.Request.Method = "GET";
        var pair = policy.Info(localRequest).MobileUrl!;
        var remoteRequest = new DefaultHttpContext();
        remoteRequest.Connection.RemoteIpAddress = IPAddress.Parse("192.168.8.177");
        remoteRequest.Request.Host = new HostString("192.168.8.138", 5099);
        remoteRequest.Request.Path = "/api/state";
        remoteRequest.Request.Method = "GET";
        Check(!policy.Allows(remoteRequest), "phone API needs a pairing key");
        remoteRequest.Request.Headers["X-Flow-Key"] = pair.Split("#pair=")[1];
        Check(policy.Allows(remoteRequest), "paired phone can read usage");
        Check(policy.Info(remoteRequest).MobileUrl is null, "pairing key is not included in remote snapshots");
        remoteRequest.Request.Method = "POST";
        remoteRequest.Request.Headers["X-Flow-Request"] = "1";
        Check(!policy.Allows(remoteRequest), "paired phones cannot change settings or submit router credentials");
        remoteRequest.Request.Path = "/api/controls/apps/0123456789ABCDEF0123/end";
        Check(policy.Allows(remoteRequest) && policy.Info(remoteRequest).CanEndProcesses, "paired phones can end a verified program process");
        remoteRequest.Request.Headers.Remove("X-Flow-Request");
        Check(!policy.Allows(remoteRequest), "phone process termination requires the same-origin action header");
        remoteRequest.Request.Headers["X-Flow-Request"] = "1";
        var correctKey = remoteRequest.Request.Headers["X-Flow-Key"].ToString();
        remoteRequest.Request.Headers["X-Flow-Key"] = "incorrect";
        Check(!policy.Allows(remoteRequest), "phone process termination rejects invalid pairing keys");
        remoteRequest.Request.Headers["X-Flow-Key"] = correctKey;
        remoteRequest.Request.Path = "/api/startup";
        Check(!policy.Allows(remoteRequest), "paired phones cannot change Windows startup");
        remoteRequest.Request.Path = "/api/controls/restore";
        Check(!policy.Allows(remoteRequest), "phone process capability cannot restore firewall rules");
        remoteRequest.Request.Path = "/api/state";
        remoteRequest.Request.Method = "GET";
        remoteRequest.Request.Headers.Origin = "https://example.com";
        Check(!policy.Allows(remoteRequest), "cross-origin phone requests are rejected");
        Check(new AccessPolicy().Info(localRequest).MobileEnabled == false, "LAN sharing is off by default");
        try { RouterClient.ParseReply("not-json"); throw new Exception("Expected parser rejection"); }
        catch (InvalidDataException) { count++; }
        var now = DateTimeOffset.Now;
        Check(Accounting.RangeStart("month", now).Day == 1, "calendar month");
        Check(Accounting.RangeStart("week", now).Date == now.Date.AddDays(-6), "seven-day inclusive range");
        try { Accounting.RangeStart("forever", now); throw new Exception("Expected range rejection"); }
        catch (ArgumentException) { count++; }
        Check(!MonitorService.IsPhysical("VPN / virtual") && MonitorService.IsPhysical("Wi-Fi"), "no virtual adapter double counting");
        var ledger = new ProcessLedger();
        var first = ledger.Start(42, "old.exe", "C:/old.exe");
        ledger.Stop(42);
        var second = ledger.Start(42, "new.exe", "C:/new.exe");
        Check(first.Name == "old.exe" && !ReferenceEquals(first, second) && !ledger.IsCurrent(first), "PID reuse keeps buffered usage with original lifetime");
        Check(ledger.IsCurrent(second), "current lifetime remains identifiable");
        ledger.Stop(42);
        Check(second.Name == "new.exe" && !ledger.IsCurrent(second), "exited programs retain their names");
        var timed = new ProcessLedger();
        var oldProcess = timed.Start(50, "first.exe", "", 100);
        timed.Stop(50, 200);
        timed.Start(50, "second.exe", "", 300);
        Check(ReferenceEquals(timed.Get(50, 150), oldProcess), "delayed network events resolve by event time");
        Check(timed.Get(50, 350).Name == "second.exe", "new PID lifetime resolves by event time");
        timed.Image(50, "C:\\Apps\\second.exe", 350);
        Check(timed.Get(50, 350).Path == "C:\\Apps\\second.exe", "executable image events enrich quota identity");
        timed.Image(50, "relative.exe", 350);
        Check(timed.Get(50, 350).Path == "C:\\Apps\\second.exe", "relative image metadata cannot replace a verified path");
        timed.Prune(250);
        Check(timed.Get(50, 150).Name.Contains("unresolved"), "expired lifetime metadata is bounded");

        var directory = Path.Combine(Path.GetTempPath(), "flow-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "test.db");
        try
        {
            using (var store = new TrafficStore(path))
            {
                ControlTests.Run(Check, store);
                var rows = new PersistRow[] { new("app", "browser", "Browser", "C:/Browser.exe", 1000, 100), new("adapter", "wifi", "WiFi", "", 1100, 120) };
                Check(store.Save(now, rows), "history save");
                Check(store.Save(now, rows), "same-minute aggregation");
                Check(store.History("day").Apps.Single().ReceivedBytes == 2000, "app aggregation");
                Check(store.Totals("day") == new UsageTotals(2200, 240), "app and adapter totals remain separate");
                store.SetSettings(new(25));
                Check(store.GetSettings().BudgetGb == 25, "settings persist");
                try { store.SetSettings(new(-1)); throw new Exception("Expected invalid budget rejection"); }
                catch (ArgumentException) { count++; }
                try { store.SetSettings(new(double.NaN)); throw new Exception("Expected NaN rejection"); }
                catch (ArgumentException) { count++; }
                var old = new DateTimeOffset(now.Date.AddDays(-2), now.Offset);
                store.Save(old, [new("adapter", "wifi", "WiFi", "", 300, 30)]);
                Check(store.Totals("day").ReceivedBytes == 2200 && store.History("week").Totals.ReceivedBytes == 2500, "date-range boundaries");
                using (var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                {
                    blocker.Open();
                    using var transaction = blocker.BeginTransaction();
                    Check(!store.Save(now, [new("adapter", "wifi", "WiFi", "", 500, 50)]), "failed writes are reported");
                    Check(store.LastError is not null, "persistence failure is visible");
                }
                Check(store.Save(now, []) && store.Totals("day").ReceivedBytes == 2700, "failed writes retried without losing or duplicating bytes");
                HistoryTests.Run(Check, store, now);
            }
            using (var reopened = new TrafficStore(path))
            {
                Check(reopened.History("day").Apps.Single().SentBytes == 200, "history survives restart");
                var oldDate = now.AddDays(-800).ToString("yyyy-MM-dd");
                Check(reopened.History("custom", oldDate, oldDate).Totals.ReceivedBytes == 800, "records older than a year survive restart");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            var connections = NativeConnections.Read();
            Check(connections.All(c => c.LocalPort is >= 0 and <= 65535 && c.Pid >= 0), "native connection table decoding");
        }
        Console.WriteLine($"PASS: {count} self-tests.");
    }
}
