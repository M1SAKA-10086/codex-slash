using System.Text;
using System.Text.Json;

namespace CodexSlash;

internal sealed record TaskState(string Thread, string Turn, string Model, string Effort, bool Active, bool Completed,
    double? Percent, string Source, DateTimeOffset Updated, int Done = 0, int Total = 0);

// Read-only adapter for Codex's local JSONL logs. No authentication, API calls or hooks are required.
internal sealed class CodexProgress : IDisposable
{
    private readonly Settings settings;
    private readonly CancellationTokenSource cancel = new();
    private readonly Dictionary<string, Tail> tails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task worker;
    private TaskState? current;
    private int count;
    internal TaskState? Current => Volatile.Read(ref current);
    internal int ActiveCount => Volatile.Read(ref count);
    internal string? Error { get; private set; }
    internal CodexProgress(Settings settings)
    { this.settings = settings; worker = Task.Run(Loop); }
    private async Task Loop()
    {
        DateTimeOffset nextScan = DateTimeOffset.MinValue;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextScan)
                {
                    nextScan = DateTimeOffset.UtcNow.AddSeconds(5);
                    var root = Path.Combine(settings.CodexHome, "sessions");
                    if (Directory.Exists(root))
                    {
                        var paths = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                            .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).Take(24).Select(f => f.FullName).ToArray();
                        // Keep already active tails even if Windows delays their LastWriteTime update.
                        foreach (var path in paths)
                            if (!tails.ContainsKey(path)) tails[path] = new Tail(path);
                        if (tails.Count > 48)
                            foreach (var key in tails.Where(p => !paths.Contains(p.Key) && p.Value.State?.Active != true).Select(p => p.Key).ToArray()) tails.Remove(key);
                    }
                    else Error = "未找到 Codex sessions 目录";
                }
                foreach (var tail in tails.Values)
                {
                    if (cancel.IsCancellationRequested) return;
                    try { tail.Poll(cancel.Token); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                var all = tails.Values.Select(t => t.State).OfType<TaskState>()
                    .Where(s => string.IsNullOrWhiteSpace(settings.ThreadId) || s.Thread == settings.ThreadId).ToArray();
                var active = all.Where(s => s.Active && s.Updated > DateTimeOffset.UtcNow.AddMinutes(-10)).OrderByDescending(s => s.Updated).ToArray();
                Volatile.Write(ref count, active.Length);
                var selected = active.FirstOrDefault() ?? all.OrderByDescending(s => s.Updated).FirstOrDefault();
                Volatile.Write(ref current, ApplyReportedProgress(selected));
                Error = null;
            }
            catch (Exception ex) { Error = ex.Message; }
            try { await Task.Delay(750, cancel.Token); } catch (OperationCanceledException) { return; }
        }
    }
    private static TaskState? ApplyReportedProgress(TaskState? state)
    {
        if (state is null || !state.Active) return state;
        var path = Path.Combine(Files.Data, "progress", state.Thread + ".json");
        if (!File.Exists(path)) return state;
        try
        {
            var report = JsonSerializer.Deserialize<ProgressReport>(File.ReadAllText(path), Files.Json);
            if (report is null || report.Thread != state.Thread || report.Turn != state.Turn || !double.IsFinite(report.Percent) || report.Percent is < 0 or > 100) return state;
            return state with { Percent = report.Percent, Source = "任务上报" };
        }
        catch { return state; }
    }
    public void Dispose() { cancel.Cancel(); /* worker owns tails; it observes cancellation during replay. */ }

    internal sealed class Tail
    {
        private readonly string path;
        private long position;
        private string partial = "";
        private Decoder decoder = Encoding.UTF8.GetDecoder();
        private string thread = "", turn = "", model = "未知模型", effort = "unknown";
        private bool subagent, active, completed;
        private double? percent;
        private string source = "等待计划进度";
        private int done, total;
        private DateTimeOffset updated = DateTimeOffset.MinValue;
        private DateTimeOffset started = DateTimeOffset.MinValue;
        internal TaskState? State => subagent || thread == "" ? null : new(thread, turn, model, effort, active, completed, percent, source, updated, done, total);
        internal Tail(string path) { this.path = path; }
        internal void Poll(CancellationToken token = default)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < position)
            { position = 0; partial = ""; decoder = Encoding.UTF8.GetDecoder(); active = completed = false; percent = null; }
            if (file.Length == position) return;
            file.Seek(position, SeekOrigin.Begin);
            var bytes = new byte[65536]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int read;
            while (!token.IsCancellationRequested && (read = file.Read(bytes)) > 0)
            {
                position += read;
                int length = decoder.GetChars(bytes, 0, read, chars, 0, false);
                string text = partial + new string(chars, 0, length);
                int start = 0, end;
                while ((end = text.IndexOf('\n', start)) >= 0)
                {
                    Consume(text.AsSpan(start, end - start)); start = end + 1;
                }
                partial = text[start..];
                if (partial.Length > 8_000_000) throw new IOException("会话日志单行超过 8 MB");
            }
        }
        internal void Consume(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty) return;
            try
            {
                using var doc = JsonDocument.Parse(line.ToString());
                var root = doc.RootElement;
                var type = Str(root, "type");
                DateTimeOffset stamp = DateTimeOffset.TryParse(Str(root, "timestamp"), out var dt) ? dt : DateTimeOffset.UtcNow;
                if (!root.TryGetProperty("payload", out var p)) return;
                if (type == "session_meta")
                {
                    thread = Str(p, "id") ?? Str(p, "session_id") ?? "";
                    if (p.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object && src.GetRawText().Contains("subagent", StringComparison.OrdinalIgnoreCase)) subagent = true;
                }
                else if (type == "turn_context")
                {
                    model = Str(p, "model") ?? model; effort = Str(p, "effort") ?? effort;
                    turn = Str(p, "turn_id") ?? turn;
                    if (p.TryGetProperty("collaboration_mode", out var cm) && cm.ValueKind == JsonValueKind.Object && cm.TryGetProperty("settings", out var cs))
                    { model = Str(cs, "model") ?? model; effort = Str(cs, "reasoning_effort") ?? effort; }
                }
                else if (type == "event_msg")
                {
                    string? eventType = Str(p, "type");
                    if (eventType == "task_started")
                    {
                        string next = Str(p, "turn_id") ?? "";
                        if (next != turn || !active)
                        { percent = null; source = "等待计划进度"; done = total = 0; }
                        turn = next; active = true; completed = false; started = stamp;
                    }
                    else if (eventType == "task_complete") { active = false; completed = true; percent = 100; source = "任务已完成"; }
                    else if (eventType is "turn_aborted" or "task_aborted" or "task_interrupted" or "task_failed")
                    { active = false; completed = false; source = "任务中断"; }
                    else if (eventType is "plan_update" or "plan_updated" or "turn_plan_updated") ReadPlan(p);
                    else if (eventType == "item_completed" && p.TryGetProperty("item", out var item))
                    {
                        var it = Str(item, "type");
                        if (it is "PlanUpdate" or "planUpdate") ReadPlan(item);
                        // Final agent text is NOT a success signal; wait for task_complete.
                    }
                    updated = stamp;
                }
                else if (type == "response_item" && Str(p, "type") == "function_call" && Str(p, "name") is "update_plan" or "functions.update_plan")
                {
                    var arguments = Str(p, "arguments");
                    if (arguments is not null) { using var a = JsonDocument.Parse(arguments); ReadPlan(a.RootElement); }
                }
            }
            catch (JsonException) { /* Older/partial/unknown events do not stop following the file. */ }
            catch (InvalidOperationException) { }
        }
        private void ReadPlan(JsonElement p)
        {
            if (!active || !p.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Array) return;
            int count = plan.GetArrayLength(); if (count == 0) return;
            done = plan.EnumerateArray().Count(e => Str(e, "status") == "completed"); total = count;
            percent = 100d * done / total; source = "计划步骤";
        }
        private static string? Str(JsonElement e, string key)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
    internal sealed record ProgressReport(string Thread, string Turn, double Percent, DateTimeOffset Updated);
    internal static void Report(string thread, string turn, double percent)
    {
        if (!Guid.TryParse(thread, out _) || string.IsNullOrWhiteSpace(turn) || !double.IsFinite(percent) || percent is < 0 or > 100)
            throw new ArgumentException("需要合法的 --thread UUID、--turn 和 0–100 的进度。");
        Files.Save(Path.Combine(Files.Data, "progress", thread + ".json"), new ProgressReport(thread, turn, percent, DateTimeOffset.UtcNow));
    }
}

internal static class ProgressPattern
{
    internal static readonly string[] Effects = ["orbit", "reverse", "bounce", "twin", "wave", "pulse"];
    internal static int LitCount(double progress) => progress <= 0 ? 0 : (int)Math.Ceiling(Math.Clamp(progress, 0, 100) * 7 / 100);
    internal static string Effect(string model, Settings s)
    {
        if (s.ModelEffects.TryGetValue(model, out var effect)) return effect;
        if (model.Contains("astra", StringComparison.OrdinalIgnoreCase)) return "twin";
        if (model.Contains("terra", StringComparison.OrdinalIgnoreCase)) return "reverse";
        if (model.Contains("luna", StringComparison.OrdinalIgnoreCase)) return "bounce";
        // Stable fallback for new models, independent of randomized .NET string hashes.
        uint hash = 2166136261; foreach (char c in model) hash = (hash ^ c) * 16777619;
        return Effects[hash % Effects.Length];
    }
    internal static byte[] Render(double? percent, string model, string effort, double seconds, double cap, double load, Settings settings)
    {
        int count = percent.HasValue ? LitCount(percent.Value) : 1;
        var b = new byte[7]; if (count == 0) return b;
        double speed = settings.EffortSpeeds.GetValueOrDefault(effort, 0.9);
        if (settings.ModulateSpeedWithLoad) speed *= 1 + Math.Clamp(load, 0, 100) / 250;
        double phase = seconds * speed;
        string effect = Effect(model, settings);
        for (int i = 0; i < count; i++)
        {
            double value;
            double x = i / (double)count;
            double head = phase - Math.Floor(phase);
            double d = Math.Min(Math.Abs(x - head), 1 - Math.Abs(x - head));
            value = effect switch
            {
                "reverse" => Glow(CircularDistance(x, 1 - head)),
                "bounce" => Math.Exp(-Math.Pow((i - (count - 1) * (1 - Math.Abs(2 * head - 1))) / 0.85, 2)),
                "twin" => Math.Max(Glow(d), Glow(CircularDistance(x, (head + 0.5) % 1))),
                "wave" => 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x - phase)),
                "pulse" => Math.Pow(0.5 + 0.5 * Math.Sin(2 * Math.PI * phase), 3),
                _ => Glow(d)
            };
            // Every completed segment remains on; only the highlight circulates inside that completed region.
            double baseline = percent.HasValue ? 0.26 : 0.07;
            b[6 - i] = (byte)Math.Clamp(Math.Round(255 * cap * (baseline + (1 - baseline) * value)), 1, 255);
        }
        return b;
    }
    private static double Glow(double d) => Math.Exp(-Math.Pow(d / 0.18, 2));
    private static double CircularDistance(double a, double b) { double d = Math.Abs(a - b); return Math.Min(d, 1 - d); }
}
