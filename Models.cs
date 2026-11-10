using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Flow;

public sealed record AppRow(string Id, string Name, string Path, int[] Pids, long ReceivedBytes, long SentBytes,
    double DownloadBps, double UploadBps, int ConnectionCount, string[] Protocols, DateTimeOffset LastSeen);
public sealed record AdapterRow(string Id, string Name, string Description, string Type, string Status,
    string[] IpAddresses, string Gateway, string Mac, long Speed, long ReceivedBytes, long SentBytes,
    double DownloadBps, double UploadBps, bool IsDefault, string? Ssid = null, string? Signal = null,
    string? LinkReceiveMbps = null, string? LinkTransmitMbps = null);
public sealed record ConnectionRow(int Pid, string App, string Protocol, string LocalAddress, int LocalPort,
    string RemoteAddress, int RemotePort, string State, string? RemoteHost = null, string? Service = null);
public sealed record Point(DateTimeOffset Time, double DownloadBps, double UploadBps);
public sealed record CollectorState(string Status, string Message, long EventsLost);
public sealed record UsageTotals(long ReceivedBytes, long SentBytes);
public sealed record Settings(double BudgetGb = 0);
public sealed record RouterLogin(string Username, string Password);
public sealed record HistoryApp(string Id, string Name, string Path, long ReceivedBytes, long SentBytes);
public sealed record DailyUsage(string Date, long ReceivedBytes, long SentBytes, int? SampledMinutes = null);
public sealed record StorageInfo(string Engine, string File, long DatabaseBytes, string? FirstDay, string? LastDay,
    int RecordedDays, int PendingMinutes, string? Error);
public sealed record HistoryResult(string Range, HistoryApp[] Apps, DailyUsage[] Daily, UsageTotals Totals,
    string StartDate, string EndDate, UsageTotals PreviousTotals, int PreviousRecordedDays, StorageInfo Storage);
public sealed record RouterDevice(string Name, string Ip, string Mac, string Type, string Signal, string TxRate, string RxRate);
public sealed record RouterState(string Status, string Message, string Model = "ZLT E200", string Firmware = "",
    string NetworkType = "", string Temperature = "", double MonthlyDownloadMb = 0, double MonthlyUploadMb = 0,
    RouterDevice[]? Devices = null, DateTimeOffset? UpdatedAt = null);
public sealed record PersistRow(string Kind, string Id, string Name, string Path, long Received, long Sent);

public static class Accounting
{
    public static long Delta(long current, long previous) => current >= previous ? current - previous : 0;
    public static string AppId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..20];
    public static bool IsLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }
    public static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    public static string SafeCsv(string text) => Csv(text.Length > 0 && "=+-@\t\r\n".Contains(text[0]) ? "'" + text : text);
    public static DateTimeOffset RangeStart(string range, DateTimeOffset now) => range switch
    {
        "day" => new DateTimeOffset(now.Date, now.Offset),
        "week" => new DateTimeOffset(now.Date.AddDays(-6), now.Offset),
        "month" => new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset),
        "30days" => new DateTimeOffset(now.Date.AddDays(-29), now.Offset),
        _ => throw new ArgumentException("Choose day, week, month, or 30days.")
    };
}
