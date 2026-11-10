using System.Text.Json;

namespace Flow;

public sealed partial class TrafficStore
{
    private void InitControls()
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_rules(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS daily_settings(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS activity(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, action TEXT NOT NULL, app TEXT NOT NULL, detail TEXT NOT NULL);
            DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT 500);
            """;
        cmd.ExecuteNonQuery();
    }
    public AppRule[] Rules()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM app_rules ORDER BY id";
            using var reader = cmd.ExecuteReader(); var rows = new List<AppRule>();
            while (reader.Read()) rows.Add(JsonSerializer.Deserialize<AppRule>(reader.GetString(0))!);
            return rows.ToArray();
        }
    }
    public void SaveRule(AppRule rule)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO app_rules VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
            cmd.Parameters.AddWithValue("$id", rule.AppId); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(rule)); cmd.ExecuteNonQuery();
        }
    }
    public void DeleteRule(string id)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM app_rules WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
    }
    public long AppToday(string id)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COALESCE(SUM(received+sent),0) FROM usage WHERE kind='app' AND id=$id AND day=$day";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$day", DateTime.Now.ToString("yyyy-MM-dd"));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
    public DailyControlSettings DailySettings()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM daily_settings WHERE id=1";
            return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<DailyControlSettings>(text)! : new();
        }
    }
    public void SaveDailySettings(DailyControlSettings settings)
    {
        if (!double.IsFinite(settings.DailyBudgetGb) || settings.DailyBudgetGb < 0 || settings.DailyBudgetGb > 100000)
            throw new ArgumentException("Daily budget must be between 0 and 100,000 GB.");
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO daily_settings VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(settings)); cmd.ExecuteNonQuery();
        }
    }
    public void LogAction(string action, string app, string detail)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO activity(time,action,app,detail) VALUES($time,$action,$app,$detail); DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT 500)";
            cmd.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O")); cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$app", app); cmd.Parameters.AddWithValue("$detail", detail); cmd.ExecuteNonQuery();
        }
    }
    public ActivityEntry[] Activity()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,time,action,app,detail FROM activity ORDER BY id DESC LIMIT 100";
            using var reader = cmd.ExecuteReader(); var rows = new List<ActivityEntry>();
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            return rows.ToArray();
        }
    }
}
