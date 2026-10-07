using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace ReflectionTimer.Core;

public record CsvSettings
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "";
    [JsonIgnore] public string ResolvedDirectory => NormalizeDirectory(Directory);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop
        ? desktop : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"), "Reflection Timer");
    public static string NormalizeDirectory(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return DefaultDirectory;
        if (!Path.IsPathFullyQualified(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException("Choose a full path for the CSV folder.");
        return Path.GetFullPath(value);
    }
}

public enum CsvDeliveryStatus { NotRequested, Pending, Saved, NeedsReview }
public record CsvWriteReply(bool Success, string File = "", string Error = "", bool Retryable = false)
{
    public string DisplayMessage => Error switch {
        "csv_format" => "The existing CSV has different columns or invalid data. Choose another folder or restore the file, then retry.",
        "csv_conflict" => "This entry ID is already in the CSV with different contents. Review the file before retrying.",
        "csv_access" => "The CSV folder cannot be written. Choose a writable folder in Settings, then retry.",
        _ => "The CSV could not be saved. Close it if another app has locked it, then retry from Outbox."
    };
}

// Each row has a stable ID. Replacing the complete file atomically makes a
// retry safe even if the app stops between the file write and profile commit.
public sealed class CsvLog
{
    public static readonly IReadOnlyList<string> Headers = Array.AsReadOnly(new[] {
        "Time", "Response", "Time spent", "Time allotted", "Status", "Reason for ending early", "Mode",
        "Pause time", "Pause Duration", "Pause Reason", "Entry ID"
    });
    public static string FileName(OutboxItem item) => (item.IsTest ? "test-" : "") + item.SubmittedAt.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture) + ".csv";
    public static string FilePath(OutboxItem item) => Path.Combine(CsvSettings.NormalizeDirectory(item.CsvDirectory), FileName(item));
    private static string Duration(long seconds) => seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    private static string PlainText(string value) => value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '@'
        || value.StartsWith('\t') || value.StartsWith('\r') ? "'" + value : value;
    internal static string[] Row(OutboxItem item)
    {
        var pauses = item.Pauses.OrderBy(p => p.PausedAt).ToArray();
        string PauseLines(Func<SessionPause, string> format) => string.Join("\n", pauses.Select((p, i) => (pauses.Length > 1 ? $"{i + 1}. " : "") + format(p)));
        return new[] {
            item.SubmittedAt.ToString("h:mm tt", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(item.Message) && (item.AutoSent || pauses.Length > 0) ? "N/A" : item.Message,
            item.ActualDurationSeconds is { } actual ? Duration(actual) : "",
            item.Mode == SessionMode.Stopwatch ? "" : Duration(item.DurationSeconds),
            string.Join(" · ", new[] { item.IsCheckIn ? "Check-in" : item.EndedEarly ? "ended early" : "", item.AutoSent ? "auto-sent" : "" }.Where(s => s.Length > 0)),
            item.EarlyEndReason, item.Mode == SessionMode.Stopwatch ? "stop watch" : "timer",
            PauseLines(p => DateTimeOffset.FromUnixTimeMilliseconds(p.PausedAt).ToOffset(item.SubmittedAt.Offset).ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture)),
            PauseLines(p => Duration((p.DurationMilliseconds ?? 0) / 1000)),
            PauseLines(p => string.IsNullOrWhiteSpace(p.Reason) ? "N/A" : p.Reason),
            (item.CsvEntryId ?? item.Id).ToString()
        }.Select(PlainText).ToArray();
    }
    public CsvWriteReply Write(OutboxItem item)
    {
        string? temporary = null;
        try {
            var file = FilePath(item);
            var mutexName = "ReflectionTimerCsv-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())));
            using var mutex = new Mutex(false, mutexName);
            bool held;
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { held = true; }
            if (!held) return new(false, Error: "csv_storage", Retryable: true);
            try {
                var row = Row(item);
                var rows = new List<string[]>();
                if (File.Exists(file) && new FileInfo(file).Length > 0) {
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var parser = new TextFieldParser(source, new UTF8Encoding(true, true), false) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
                    parser.SetDelimiters(",");
                    if (!Headers.SequenceEqual(parser.ReadFields() ?? [])) return new(false, Error: "csv_format");
                    while (!parser.EndOfData) {
                        var existing = parser.ReadFields()!;
                        if (existing.Length != Headers.Count) return new(false, Error: "csv_format");
                        if (existing[^1] == row[^1]) return existing.SequenceEqual(row) ? new(true, file) : new(false, Error: "csv_conflict");
                        rows.Add(existing);
                    }
                }
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true)) {
                        writer.NewLine = "\r\n";
                        void WriteRow(IEnumerable<string> fields) => writer.WriteLine(string.Join(",", fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
                        WriteRow(Headers);
                        foreach (var existing in rows) WriteRow(existing);
                        WriteRow(row);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                File.Move(temporary, file, overwrite: true);
                temporary = null;
                return new(true, file);
            } finally { mutex.ReleaseMutex(); }
        } catch (MalformedLineException) { return new(false, Error: "csv_format"); }
        catch (DecoderFallbackException) { return new(false, Error: "csv_format"); }
        catch (UnauthorizedAccessException) { return new(false, Error: "csv_access"); }
        catch (IOException) { return new(false, Error: "csv_storage", Retryable: true); }
        catch (ArgumentException) { return new(false, Error: "csv_access"); }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch { } } }
    }
}
