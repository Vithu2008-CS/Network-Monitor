namespace Flow;

public sealed record AppRule(string AppId, string Name, string Path, double DailyLimitMb = 0,
    double UploadLimitKbps = 0, string QuotaAction = "alert", bool Blocked = false, bool Enabled = true,
    string ResetDay = "", long ResetBytes = 0);
public sealed record RuleRequest(string AppId, double DailyLimitMb = 0, double UploadLimitKbps = 0,
    string QuotaAction = "alert", bool Blocked = false, bool Enabled = true);
public sealed record DailyBudgetRequest(double DailyBudgetGb);
public sealed record DailyControlSettings(double DailyBudgetGb = 0, string ResetDay = "", long ResetBytes = 0);
public sealed record ActivityEntry(long Id, DateTimeOffset Time, string Action, string App, string Detail);
public sealed record ProcessTarget(int Pid, string Name, string StartToken, bool CanEnd, string Reason, long MemoryBytes = 0, double CpuSeconds = 0);
public sealed record EndProcessRequest(int Pid, string StartToken);
public sealed record NativeRuleState(bool Blocked, double UploadLimitKbps);
public sealed record RuleView(AppRule Rule, long UsedBytes, long LimitBytes, long RemainingBytes,
    bool Exceeded, bool ShouldBlock, string Status, string? Error);

public static class Quotas
{
    public static void Validate(RuleRequest request)
    {
        if (!double.IsFinite(request.DailyLimitMb) || request.DailyLimitMb < 0 || request.DailyLimitMb > 100000000 ||
            !double.IsFinite(request.UploadLimitKbps) || request.UploadLimitKbps < 0 || request.UploadLimitKbps > 10000000 ||
            request.UploadLimitKbps is > 0 and < 1 || request.QuotaAction is not ("alert" or "block"))
            throw new ArgumentException("Use a valid daily allowance, upload limit of at least 1 KB/s (or 0), and an alert or block action.");
    }
    public static long Used(long total, string resetDay, long baseline, string today) => Math.Max(0, total - (resetDay == today ? baseline : 0));
    public static bool Exceeded(long used, double dailyLimitMb) => dailyLimitMb > 0 && used >= dailyLimitMb * 1000000d;
    public static bool ShouldBlock(AppRule rule, long used) => rule.Enabled && (rule.Blocked || rule.QuotaAction == "block" && Exceeded(used, rule.DailyLimitMb));
}
