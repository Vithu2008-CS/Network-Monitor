using System.Globalization;

namespace Flow;

public sealed partial class TrafficStore
{
    public HistoryResult History(string range, string? startDate = null, string? endDate = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var end = today;
        var start = DateOnly.FromDateTime(Accounting.RangeStart(range == "custom" ? "day" : range, DateTimeOffset.Now).Date);
        if (startDate is not null || endDate is not null || range == "custom")
        {
            if (!DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
                !DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
                throw new ArgumentException("Enter both dates using YYYY-MM-DD.");
            range = "custom";
        }
        if (start > end || end > today || end.DayNumber - start.DayNumber >= 366 || start.DayNumber < 366)
            throw new ArgumentException("Choose an ordered date range of up to 366 days, ending today or earlier.");
        var from = start.ToString("yyyy-MM-dd");
        var until = end.ToString("yyyy-MM-dd");
        var length = end.DayNumber - start.DayNumber + 1;
        lock (gate)
        {
            var apps = new List<HistoryApp>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id,MAX(name),MAX(path),SUM(received),SUM(sent) FROM usage
                    WHERE kind='app' AND day BETWEEN $start AND $end
                    GROUP BY id ORDER BY SUM(received)+SUM(sent) DESC, id
                    """;
                cmd.Parameters.AddWithValue("$start", from);
                cmd.Parameters.AddWithValue("$end", until);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) apps.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
            var days = new List<DailyUsage>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    WITH dates AS (
                        SELECT day FROM usage WHERE day BETWEEN $start AND $end
                        UNION SELECT day FROM collection WHERE day BETWEEN $start AND $end
                    ), traffic AS (
                        SELECT day,SUM(received) received,SUM(sent) sent FROM usage
                        WHERE kind='adapter' AND day BETWEEN $start AND $end GROUP BY day
                    ), samples AS (
                        SELECT day,COUNT(*) minutes FROM collection
                        WHERE day BETWEEN $start AND $end GROUP BY day
                    )
                    SELECT dates.day,COALESCE(received,0),COALESCE(sent,0),minutes
                    FROM dates LEFT JOIN traffic USING(day) LEFT JOIN samples USING(day) ORDER BY dates.day
                    """;
                cmd.Parameters.AddWithValue("$start", from);
                cmd.Parameters.AddWithValue("$end", until);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) days.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }
            UsageTotals previous;
            int previousDays;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(SUM(received),0),COALESCE(SUM(sent),0),
                      (SELECT COUNT(*) FROM (
                        SELECT day FROM usage WHERE day BETWEEN $start AND $end
                        UNION SELECT day FROM collection WHERE day BETWEEN $start AND $end))
                    FROM usage WHERE kind='adapter' AND day BETWEEN $start AND $end
                    """;
                cmd.Parameters.AddWithValue("$start", start.AddDays(-length).ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$end", start.AddDays(-1).ToString("yyyy-MM-dd"));
                using var reader = cmd.ExecuteReader();
                reader.Read();
                previous = new(reader.GetInt64(0), reader.GetInt64(1));
                previousDays = reader.GetInt32(2);
            }
            return new(range, apps.ToArray(), days.ToArray(),
                new(days.Sum(d => d.ReceivedBytes), days.Sum(d => d.SentBytes)), from, until, previous, previousDays, Storage());
        }
    }

    public StorageInfo Storage()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT MIN(day),MAX(day),COUNT(*) FROM (SELECT day FROM usage UNION SELECT day FROM collection)";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var first = reader.IsDBNull(0) ? null : reader.GetString(0);
            var last = reader.IsDBNull(1) ? null : reader.GetString(1);
            var days = reader.GetInt32(2);
            reader.Close();
            cmd.CommandText = "PRAGMA page_count";
            var pages = Convert.ToInt64(cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA page_size";
            var size = Convert.ToInt64(cmd.ExecuteScalar());
            return new("SQLite", System.IO.Path.GetFullPath(db.DataSource), pages * size, first, last, days, pendingMinutes.Count, LastError);
        }
    }
}
