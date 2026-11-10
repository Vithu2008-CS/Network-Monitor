namespace Flow;

internal static class ControlTests
{
    public static void Run(Action<bool, string> check, TrafficStore store)
    {
        Quotas.Validate(new("test", 100, 64, "block"));
        check(Quotas.Used(500, "today", 300, "today") == 200, "quota reset subtracts baseline without deleting history");
        check(Quotas.Used(500, "yesterday", 300, "today") == 500, "quota baseline automatically resets each local day");
        check(Quotas.Used(50, "today", 300, "today") == 0, "quota cannot go negative");
        var rule = new AppRule("test", "Test", "C:/test.exe", 1, 0, "block");
        check(!Quotas.ShouldBlock(rule, 999999) && Quotas.ShouldBlock(rule, 1000000), "daily byte limit boundary");
        check(!Quotas.ShouldBlock(rule with { Enabled = false }, 2000000), "disabled quotas never block");
        check(!Quotas.ShouldBlock(rule with { QuotaAction = "alert" }, 2000000), "alert-only quota cannot block");
        check(Quotas.ShouldBlock(rule with { Blocked = true }, 0), "manual block does not depend on quota");
        foreach (var invalid in new[] { new RuleRequest("x", double.NaN), new RuleRequest("x", -1), new RuleRequest("x", 0, 0.5), new RuleRequest("x", 0, 1, "kill") })
        {
            try { Quotas.Validate(invalid); throw new Exception("Expected rule rejection"); }
            catch (ArgumentException) { check(true, "rule validation"); }
        }
        check(ProcessControls.ProtectionReason(Environment.ProcessPath!) is not null, "monitor cannot control its own executable");
        check(ProcessControls.ProtectionReason(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe")) is not null, "Windows components protected");
        store.SaveRule(rule); check(store.Rules().Single().DailyLimitMb == 1, "app rules persist");
        store.SaveRule(rule with { DailyLimitMb = 2 }); check(store.Rules().Single().DailyLimitMb == 2, "app rules can be updated");
        store.SaveDailySettings(new(5)); check(store.DailySettings().DailyBudgetGb == 5, "daily settings persist");
        store.LogAction("Test", "Test app", "Local test only"); check(store.Activity().First().Action == "Test", "control activity is recorded");
        store.DeleteRule(rule.AppId); check(store.Rules().Length == 0, "rules can be removed");
    }
}
