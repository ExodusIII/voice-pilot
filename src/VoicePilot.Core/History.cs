using System.Text.Json;

namespace VoicePilot.Core;

public sealed class StepRecord
{
    public string Text { get; set; } = "";       // human-readable step ("open_app "Google Chrome"")
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "pending";   // pending | running | ok | error | skipped
    public string Message { get; set; } = "";
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Time { get; set; } = Json.Iso(DateTime.Now);
    public string Text { get; set; } = "";
    public string Input { get; set; } = "voice";      // voice | typed | wake | macro
    public string Lang { get; set; } = "en";
    public string Source { get; set; } = "";          // local | claude | claude-cli | macro | none
    public string Status { get; set; } = "running";   // running | ok | error | cancelled | unknown
    public string Reply { get; set; } = "";
    public List<StepRecord> Steps { get; set; } = new();
    public double SttSeconds { get; set; }
    public double ThinkSeconds { get; set; }
    public double TotalSeconds { get; set; }
}

/// The last 200 commands, persisted to disk.
public sealed class History
{
    const int Max = 200;
    readonly object _gate = new();
    static readonly object FileGate = new();
    readonly List<HistoryEntry> _items = new();
    Timer? _saveTimer;

    public History()
    {
        try
        {
            if (File.Exists(Paths.HistoryFile))
                _items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Paths.HistoryFile), Json.Web) ?? new();
        }
        catch { }
        // Entries interrupted by an app exit.
        foreach (var e in _items.Where(e => e.Status == "running")) e.Status = "cancelled";
    }

    public List<HistoryEntry> Snapshot(int count = Max)
    {
        lock (_gate) return _items.TakeLast(count).Reverse().ToList();
    }

    public List<HistoryEntry> Recent(int count)
    {
        lock (_gate) return _items.TakeLast(count).ToList();
    }

    public void Add(HistoryEntry e)
    {
        lock (_gate)
        {
            _items.Add(e);
            if (_items.Count > Max) _items.RemoveRange(0, _items.Count - Max);
        }
        Changed();
    }

    public void Changed()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => Save(), null, 1500, Timeout.Infinite);
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        Save();
    }

    public void Save()
    {
        try
        {
            List<HistoryEntry> items;
            lock (_gate) items = _items.ToList();
            // Entries may be updated while a command runs; each is locked while serialized.
            var json = "[" + string.Join(",", items.Select(e => { lock (e) return JsonSerializer.Serialize(e, Json.Web); })) + "]";
            lock (FileGate)
            {
                Directory.CreateDirectory(Paths.Data);
                var tmp = Paths.HistoryFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, Paths.HistoryFile, overwrite: true);
            }
        }
        catch (Exception ex) { Log.Error("history save failed", ex); }
    }
}
