using System.Text.Json;

namespace CodexSlash;

internal static class SelfTests
{
    internal static int Run(string? output)
    {
        var tests = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); tests.Add(name); }
        try
        {
            var s = new Settings(); var p = new GProfile(2, 8, 0, false, false, null);
            Check(Pattern.BlockReason(false, false, false, true, p, s, false) is null, "Codex running takes ownership");
            Check(Pattern.BlockReason(false, false, false, false, p, s, false) is not null, "Codex absent releases ownership");
            Check(Pattern.BlockReason(true, false, false, true, p, s, false) is not null, "Pause releases ownership");
            Check(Pattern.BlockReason(false, true, false, true, p, s, false) is not null, "Suspend releases ownership");
            Check(Pattern.BlockReason(false, false, true, true, p, s, false) is not null, "Lock releases ownership");
            Check(Pattern.BlockReason(false, false, false, true, p with { Brightness = 0 }, s, false) is not null, "G-Helper disabled is honored");
            Check(Pattern.BlockReason(false, false, false, true, p with { Mode = 21 }, s, false) is not null, "Dark mode is honored");
            Check(Pattern.BlockReason(false, false, false, true, p with { BatteryOff = true }, s, true) is not null, "Battery off policy is honored");
            foreach (var m in new[] { 19, 20, 22 }) Check(Pattern.BlockReason(false, false, false, true, p with { Mode = m }, s, false) is not null, $"Streaming mode {m} yields to G-Helper");
            Check(Pattern.BlockReason(false, false, false, true, p with { Mode = 99 }, s, false) is not null, "Unknown mode yields safely");
            byte[] Render(double? percent, double sec = 0, string model = "gpt-6.1-sol", string effort = "high") => ProgressPattern.Render(percent, model, effort, sec, 0.6, 0, s);
            Check(Render(0).All(v => v == 0), "Zero completion lights zero progress segments");
            Check(Render(null).Count(v => v > 0) == 7, "Unknown progress animates the whole bar without inventing a percentage");
            Check(!Render(null, 0).SequenceEqual(Render(null, 0.21)), "Unknown progress keeps moving and breathing");
            Check(!Render(null, 0.27, effort: "low").SequenceEqual(Render(null, 0.27, effort: "high")), "Unknown progress follows reasoning effort");
            Check(!Render(null, 0.27).SequenceEqual(Render(null, 0.27, model: "gpt-6.1-terra")), "Unknown progress follows model style");
            Check(ProgressPattern.Effects.All(effect => {
                var ambientSettings = s with { ModelEffects = new() { ["ambient-test"] = effect } };
                return !ProgressPattern.Render(null, "ambient-test", "high", 0, 0.6, 0, ambientSettings)
                    .SequenceEqual(ProgressPattern.Render(null, "ambient-test", "high", 0.21, 0.6, 0, ambientSettings));
            }), "All six styles animate without reported progress");
            Check(Render(10).Count(v => v > 0) == 1 && Render(20).Count(v => v > 0) == 2, "10 percent is one segment, 20 percent is two");
            Check(Render(100).Count(v => v > 0) == 7, "100 percent lights seven segments");
            Check(Render(100).All(v => v <= 153), "Brightness ceiling enforced");
            Check(Render(-10).All(v => v == 0) && Render(150).Count(v => v > 0) == 7, "Out of range progress clamped");
            Check(Enumerable.Range(0, 100).All(n => Render(40, n * 0.173).Count(v => v > 0) == 3), "Completed segments remain lit across 100 animation frames");
            Check(!Render(70, 0).SequenceEqual(Render(70, 0.21)), "Highlight circulates while segment count is constant");
            Check(!Render(100, 0.27, effort: "low").SequenceEqual(Render(100, 0.27, effort: "high")), "Reasoning effort changes animation cadence");
            Check(!Render(100, 0.27).SequenceEqual(Render(100, 0.27, model: "gpt-6.1-terra")), "Model changes animation style");
            Check(ProgressPattern.Effect("new-model", s) == ProgressPattern.Effect("new-model", s), "Unknown model has a stable fallback");
            var tail = new CodexProgress.Tail("unused");
            string Event(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = DateTimeOffset.UtcNow, payload });
            tail.Consume(Event("session_meta", new { id = "test-thread", source = "vscode" }));
            tail.Consume(Event("event_msg", new { type = "task_started", turn_id = "turn1" }));
            tail.Consume(Event("turn_context", new { model = "gpt-6.1-sol", effort = "high", turn_id = "turn1" }));
            Check(tail.State is { Active: true, Percent: null, Model: "gpt-6.1-sol", Effort: "high" }, "Real turn schema supplies model/effort without invented progress");
            var plan = Enumerable.Range(0, 10).Select(n => new { step = "step" + n, status = n < 2 ? "completed" : "pending" }).ToArray();
            tail.Consume(Event("event_msg", new { type = "plan_update", plan }));
            Check(tail.State is { Percent: 20, Done: 2, Total: 10 }, "Plan completion ratio produces 20 percent");
            tail.Consume(Event("event_msg", new { type = "task_complete" }));
            Check(tail.State is { Completed: true, Active: false, Percent: 100 }, "Success event reaches 100 percent");
            tail.Consume(Event("event_msg", new { type = "task_started", turn_id = "turn2" }));
            Check(tail.State is { Active: true, Percent: null }, "New turn resets previous progress");
            tail.Consume(Event("event_msg", new { type = "turn_aborted" }));
            Check(tail.State is { Completed: false, Active: false, Percent: null }, "Interrupt does not become successful completion");
            var sub = new CodexProgress.Tail("unused");
            sub.Consume(Event("session_meta", new { id = "child", source = new { subagent = new { thread_id = "parent" } } }));
            Check(sub.State is null, "Subagent logs do not steal the primary thread display");
            var fixture = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output ?? Files.Data + "/self-test.json"))!, "tail-fixture.jsonl");
            File.WriteAllText(fixture, Event("session_meta", new { id = "fixture", source = "vscode" }) + "\n" + Event("event_msg", new { type = "task_started", turn_id = "fixture-turn" }) + "\n");
            var replay = new CodexProgress.Tail(fixture); replay.Poll();
            string finish = Event("event_msg", new { type = "task_complete" });
            File.AppendAllText(fixture, finish[..15]); replay.Poll();
            Check(replay.State is { Active: true }, "Partial JSONL writes wait for a complete line");
            File.AppendAllText(fixture, finish[15..] + "\n"); replay.Poll();
            Check(replay.State is { Completed: true }, "Incremental JSONL replay finishes the correct task");
            File.Delete(fixture);
            var packet = SlashHid.Packet(0xD3, 0, 0, 7, 1, 2, 3, 4, 5, 6, 7);
            Check(packet.Length == 128 && packet[0] == 0x5E && packet[4] == 7 && packet[11] == 7, "Correct HID report and segment count");
            var snapshot = new Snapshot("device", p, new byte[128], new byte[128], new byte[128]);
            var roundtrip = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(snapshot, Files.Json), Files.Json)!;
            Check(roundtrip.Profile == p && roundtrip.Pool.Length == 128, "Crash recovery journal round trip");
            Check(Settings.Load().ProcessNames.Length > 0, "Shipped configuration loads");
            Files.Save(output ?? System.IO.Path.Combine(Files.Data, "self-test.json"), new { ok = true, passed = tests.Count, tests }); return 0;
        }
        catch (Exception ex)
        { Files.Save(output ?? System.IO.Path.Combine(Files.Data, "self-test.json"), new { ok = false, passed = tests.Count, tests, error = ex.ToString() }); return 1; }
    }
}
