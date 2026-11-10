using System.ComponentModel;
using System.Security.Principal;

namespace Flow;

public sealed class ControlService(TrafficStore store, MonitorService monitor, IWindowsRules windows, ILogger<ControlService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (string Status, string? Error)> states = [];
    private readonly HashSet<string> notices = [];
    private readonly bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private string? serviceError;
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");
    private void RequireAdmin() { if (!admin) throw new ArgumentException("Start the monitor as administrator to change application controls."); }

    private AppRule KnownApp(string id)
    {
        var saved = store.Rules().FirstOrDefault(r => r.AppId == id);
        var known = monitor.FindApp(id);
        var result = saved ?? (known is null ? null : new AppRule(known.Id, known.Name, known.Path));
        if (result is null) throw new ArgumentException("Select an application observed by this monitor.");
        if (ProcessControls.ProtectionReason(result.Path) is { } reason) throw new ArgumentException(reason);
        if (Accounting.AppId(result.Path) != id) throw new ArgumentException("The executable identity changed. Select it again.");
        return result;
    }

    private RuleView View(AppRule rule)
    {
        var used = Quotas.Used(store.AppToday(rule.AppId), rule.ResetDay, rule.ResetBytes, Today);
        var limit = (long)(rule.DailyLimitMb * 1000000d);
        var state = states.GetValueOrDefault(rule.AppId, (admin ? "pending" : "needs-admin", null));
        return new(rule, used, limit, Math.Max(0, limit - used), Quotas.Exceeded(used, rule.DailyLimitMb), Quotas.ShouldBlock(rule, used), state.Item1, state.Item2);
    }
    public async Task<object> Snapshot(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var daily = store.DailySettings(); var totals = store.Totals("day");
            var used = Quotas.Used(totals.ReceivedBytes + totals.SentBytes, daily.ResetDay, daily.ResetBytes, Today);
            var limit = daily.DailyBudgetGb * 1e9;
            return new { isAdministrator = admin, rules = store.Rules().Select(View).ToArray(),
                daily = new { budgetGb = daily.DailyBudgetGb, usedBytes = used, remainingBytes = Math.Max(0, limit - used), exceeded = limit > 0 && used >= limit, resetAt = DateTimeOffset.Now.Date.AddDays(1) },
                activity = store.Activity(), serviceError };
        }
        finally { gate.Release(); }
    }
    private async Task Apply(AppRule rule, CancellationToken token)
    {
        try
        {
            if (ProcessControls.ProtectionReason(rule.Path) is { } reason && rule.Enabled) throw new InvalidOperationException(reason);
            var view = View(rule);
            await windows.Apply(rule, view.ShouldBlock, token);
            states[rule.AppId] = (rule.Enabled ? view.ShouldBlock ? "blocked" : rule.UploadLimitKbps > 0 ? "limited" : "watching" : "disabled", null);
            var notice = Today + ":" + rule.AppId + ":" + rule.ResetBytes;
            if (rule.Enabled && view.Exceeded && notices.Add(notice)) store.LogAction("Quota reached", rule.Name, rule.QuotaAction == "block" ? "Network block applied at the daily allowance." : "Daily allowance exceeded; alert only.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!states.TryGetValue(rule.AppId, out var previous) || previous.Error != ex.Message) store.LogAction("Rule error", rule.Name, ex.Message);
            states[rule.AppId] = ("error", ex.Message);
        }
    }
    public async Task Save(RuleRequest request, CancellationToken token)
    {
        RequireAdmin(); Quotas.Validate(request);
        await gate.WaitAsync(token);
        try
        {
            var known = KnownApp(request.AppId);
            var rule = known with { DailyLimitMb = request.DailyLimitMb, UploadLimitKbps = request.UploadLimitKbps, QuotaAction = request.QuotaAction, Blocked = request.Blocked, Enabled = request.Enabled };
            store.SaveRule(rule); await Apply(rule, token);
            store.LogAction("Rule saved", rule.Name, $"Daily allowance {rule.DailyLimitMb} MB; upload {rule.UploadLimitKbps} KB/s; {rule.QuotaAction}; enabled={rule.Enabled}.");
        }
        finally { gate.Release(); }
    }
    public async Task Reset(string id, CancellationToken token)
    {
        RequireAdmin(); await gate.WaitAsync(token);
        try
        {
            var rule = store.Rules().FirstOrDefault(r => r.AppId == id) ?? throw new ArgumentException("No rule exists for this application.");
            rule = rule with { ResetDay = Today, ResetBytes = store.AppToday(id) };
            store.SaveRule(rule); await Apply(rule, token); store.LogAction("Quota reset", rule.Name, "Today's quota starts from zero; usage history was retained. Manual blocks remain unchanged.");
        }
        finally { gate.Release(); }
    }
    public async Task Remove(string id, CancellationToken token)
    {
        RequireAdmin(); await gate.WaitAsync(token);
        try { await windows.Remove(id, token); store.DeleteRule(id); states.Remove(id); store.LogAction("Rule removed", id, "Flow network controls removed."); }
        finally { gate.Release(); }
    }
    public async Task Restore(CancellationToken token)
    {
        RequireAdmin(); await gate.WaitAsync(token);
        try
        {
            await windows.Clear(token);
            foreach (var rule in store.Rules()) store.SaveRule(rule with { Enabled = false, Blocked = false });
            states.Clear(); serviceError = null; store.LogAction("Access restored", "All applications", "All Flow rules disabled and their Windows controls removed. Other firewall policies were untouched.");
        }
        finally { gate.Release(); }
    }
    public void Daily(DailyBudgetRequest request, bool reset)
    {
        RequireAdmin(); var settings = store.DailySettings();
        if (reset) { var total = store.Totals("day"); settings = settings with { ResetDay = Today, ResetBytes = total.ReceivedBytes + total.SentBytes }; }
        else settings = settings with { DailyBudgetGb = request.DailyBudgetGb };
        store.SaveDailySettings(settings); store.LogAction(reset ? "Daily budget reset" : "Daily budget saved", "Laptop", "Alert-only budget; usage history was retained.");
    }
    public ProcessTarget[] Processes(string id) => ProcessControls.List(KnownApp(id).Path);
    public void End(string id, EndProcessRequest request)
    {
        RequireAdmin(); var rule = KnownApp(id);
        ProcessControls.End(rule.Path, request); store.LogAction("Process ended", rule.Name, $"PID {request.Pid}. Only that process was terminated.");
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!admin) return;
        try
        {
            try { await windows.Clear(token); }
            catch (Exception ex) { serviceError = "Could not clean up previous controls: " + ex.Message; }
            while (!token.IsCancellationRequested)
            {
                await gate.WaitAsync(token);
                try
                {
                    foreach (var rule in store.Rules()) await Apply(rule, token);
                    var daily = store.DailySettings(); var totals = store.Totals("day");
                    var used = Quotas.Used(totals.ReceivedBytes + totals.SentBytes, daily.ResetDay, daily.ResetBytes, Today);
                    if (daily.DailyBudgetGb > 0 && used >= daily.DailyBudgetGb * 1e9 && notices.Add("laptop:" + Today + ":" + daily.ResetBytes)) store.LogAction("Daily budget reached", "Laptop", "Daily alert-only budget exceeded.");
                }
                finally { gate.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await windows.Clear(timeout.Token); }
            catch (Exception ex) { logger.LogError("Control cleanup failed. Run Restore Access.cmd: {Error}", ex.Message); }
        }
    }
}
