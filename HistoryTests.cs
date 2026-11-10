namespace Flow;

public static class HistoryTests
{
    public static void Run(Action<bool, string> check, TrafficStore store, DateTimeOffset now)
    {
        var today = now.ToString("yyyy-MM-dd");
        var report = store.History("custom", today, today);
        check(report.StartDate == today && report.EndDate == today, "custom history includes both date boundaries");
        check(report.Totals == store.Totals("day"), "custom and preset totals agree");
        check(report.Daily.Single().SampledMinutes == 1, "repeated samples in a minute count once");
        var yesterday = new DateTimeOffset(now.Date.AddDays(-1), now.Offset);
        check(store.Save(yesterday, []), "idle sample is saved");
        var idle = store.History("custom", yesterday.ToString("yyyy-MM-dd"), yesterday.ToString("yyyy-MM-dd"));
        check(idle.Daily.Single().ReceivedBytes == 0 && idle.Daily.Single().SampledMinutes == 1, "idle collection remains distinguishable from missing records");
        check(idle.Apps.Length == 0, "app breakdown respects exact selected day");
        var tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");
        foreach (var (start, end) in new (string?, string?)[]
        {
            (today, null), ("bad-date", today), (tomorrow, today), (today, tomorrow),
            (now.AddDays(-366).ToString("yyyy-MM-dd"), today), ("0001-01-01", "0001-01-01")
        })
        {
            try { store.History("custom", start, end); throw new Exception("Expected invalid history range rejection"); }
            catch (ArgumentException) { check(true, "invalid history dates rejected"); }
        }
        var previous = now.AddDays(-7);
        store.Save(previous, [new("adapter", "wifi", "Wi-Fi", "", 700, 70)]);
        check(store.History("week").PreviousTotals == new UsageTotals(700, 70), "comparison uses immediately preceding equal-length period");
        var old = now.AddDays(-800);
        store.Save(old, [new("adapter", "wifi", "Wi-Fi", "", 800, 80)]);
        check(store.Storage().FirstDay == old.ToString("yyyy-MM-dd") && store.Storage().DatabaseBytes > 0, "storage reports retained dates and allocated bytes");
        check(store.Storage().PendingMinutes == 0 && store.Storage().Error is null, "successful save clears pending status");
    }
}
