using Microsoft.Data.Sqlite;

namespace Flow;

public sealed partial class TrafficStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private readonly Dictionary<(long Minute, string Kind, string Id), (DateTimeOffset Time, PersistRow Row)> unsaved = [];
    private readonly Dictionary<long, string> pendingMinutes = [];
    public string? LastError { get; private set; }

    public TrafficStore(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1 }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS usage (
              minute INTEGER NOT NULL, day TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL,
              name TEXT NOT NULL, path TEXT NOT NULL, received INTEGER NOT NULL, sent INTEGER NOT NULL,
              PRIMARY KEY (minute,kind,id));
            CREATE INDEX IF NOT EXISTS usage_day ON usage(day,kind);
            CREATE TABLE IF NOT EXISTS collection (minute INTEGER PRIMARY KEY, day TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS collection_day ON collection(day);
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK (id=1), budget REAL NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        InitControls();
    }

    public bool Save(DateTimeOffset time, IEnumerable<PersistRow> rows)
    {
        lock (gate)
        {
            pendingMinutes[time.ToUnixTimeSeconds() / 60] = time.ToLocalTime().ToString("yyyy-MM-dd");
            foreach (var row in rows.Where(r => r.Received != 0 || r.Sent != 0))
            {
                var key = (time.ToUnixTimeSeconds() / 60, row.Kind, row.Id);
                unsaved[key] = unsaved.TryGetValue(key, out var previous)
                    ? (previous.Time, row with { Received = previous.Row.Received + row.Received, Sent = previous.Row.Sent + row.Sent })
                    : (time, row);
            }
            try
            {
                using var transaction = db.BeginTransaction();
                foreach (var (minute, day) in pendingMinutes)
                {
                    using var sample = db.CreateCommand();
                    sample.Transaction = transaction;
                    sample.CommandText = "INSERT OR IGNORE INTO collection VALUES($minute,$day)";
                    sample.Parameters.AddWithValue("$minute", minute);
                    sample.Parameters.AddWithValue("$day", day);
                    sample.ExecuteNonQuery();
                }
                foreach (var (sampleTime, row) in unsaved.Values)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = """
                        INSERT INTO usage VALUES($minute,$day,$kind,$id,$name,$path,$received,$sent)
                        ON CONFLICT(minute,kind,id) DO UPDATE SET received=received+excluded.received,sent=sent+excluded.sent;
                        """;
                    cmd.Parameters.AddWithValue("$minute", sampleTime.ToUnixTimeSeconds() / 60);
                    cmd.Parameters.AddWithValue("$day", sampleTime.ToLocalTime().ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$kind", row.Kind);
                    cmd.Parameters.AddWithValue("$id", row.Id);
                    cmd.Parameters.AddWithValue("$name", row.Name);
                    cmd.Parameters.AddWithValue("$path", row.Path);
                    cmd.Parameters.AddWithValue("$received", row.Received);
                    cmd.Parameters.AddWithValue("$sent", row.Sent);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
                unsaved.Clear();
                pendingMinutes.Clear();
                LastError = null;
                return true;
            }
            catch (SqliteException)
            {
                LastError = "History could not be saved. Measurements are buffered in memory and will retry automatically. Check free disk space; keep the monitor open.";
                return false;
            }
        }
    }

    public UsageTotals Totals(string range)
    {
        var start = Accounting.RangeStart(range, DateTimeOffset.Now).ToString("yyyy-MM-dd");
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(received),0),COALESCE(SUM(sent),0) FROM usage WHERE kind='adapter' AND day >= $start";
            cmd.Parameters.AddWithValue("$start", start);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new(reader.GetInt64(0), reader.GetInt64(1));
        }
    }

    public Settings GetSettings()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT budget FROM settings WHERE id=1";
            return new(Convert.ToDouble(cmd.ExecuteScalar() ?? 0));
        }
    }

    public void SetSettings(Settings settings)
    {
        if (!double.IsFinite(settings.BudgetGb) || settings.BudgetGb < 0 || settings.BudgetGb > 100000)
            throw new ArgumentException("Budget must be between 0 and 100,000 GB.");
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings VALUES(1,$budget) ON CONFLICT(id) DO UPDATE SET budget=excluded.budget";
            cmd.Parameters.AddWithValue("$budget", settings.BudgetGb);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => db.Dispose();
}
