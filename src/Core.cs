using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexSlash;

internal static class Files
{
    internal static readonly string Data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSlash");
    internal static readonly string GHelper = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper", "config.json");
    internal static readonly string Journal = System.IO.Path.Combine(Data, "restore.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true);
    }
    internal static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Data);
            var p = System.IO.Path.Combine(Data, "app.log");
            if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.Move(p, p + ".old", true);
            File.AppendAllText(p, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
        catch { /* Logging must never prevent hardware restoration. */ }
    }
}

internal sealed record Settings
{
    public string[] ProcessNames { get; init; } = ["Codex"];
    public bool IncludeGpu { get; init; } = true;
    public double BrightnessLimit { get; init; } = 0.85;
    public double Smoothing { get; init; } = 0.3;
    public int SampleMilliseconds { get; init; } = 1000;
    public bool RespectGHelperOff { get; init; } = true;
    public string CodexHome { get; init; } = Environment.GetEnvironmentVariable("CODEX_HOME") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string? ThreadId { get; init; }
    public bool ModulateSpeedWithLoad { get; init; } = true;
    public Dictionary<string, string> ModelEffects { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6.1-sol"] = "orbit", ["gpt-6-sol"] = "wave", ["gpt-6-astra"] = "twin",
        ["gpt-6.1-terra"] = "reverse", ["gpt-5.6-terra"] = "bounce", ["gpt-6-luna"] = "pulse", ["gpt-5.6-luna"] = "bounce",
        ["gpt-5.6-sol"] = "reverse"
    };
    public Dictionary<string, double> EffortSpeeds { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    { ["none"] = 0.3, ["minimal"] = 0.45, ["low"] = 0.6, ["medium"] = 0.9, ["high"] = 1.25, ["xhigh"] = 1.65, ["max"] = 2.0, ["ultra"] = 2.4 };
    internal static Settings Load()
    {
        var p = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p), Files.Json) ?? throw new IOException("settings.json 为空");
        if (s.ProcessNames is not { Length: > 0 } || s.ProcessNames.Any(string.IsNullOrWhiteSpace)) throw new IOException("ProcessNames 至少需要一个进程名");
        if (!double.IsFinite(s.BrightnessLimit) || s.BrightnessLimit is < 0.05 or > 1) throw new IOException("BrightnessLimit 范围为 0.05–1");
        if (!double.IsFinite(s.Smoothing) || s.Smoothing is <= 0 or > 1) throw new IOException("Smoothing 范围为 0–1（不含 0）");
        if (s.SampleMilliseconds is < 500 or > 10000) throw new IOException("SampleMilliseconds 范围为 500–10000");
        if (s.ModelEffects is null || s.ModelEffects.Values.Any(v => !ProgressPattern.Effects.Contains(v))) throw new IOException("ModelEffects 可用值：orbit / reverse / bounce / twin / wave / pulse");
        if (s.EffortSpeeds is null || s.EffortSpeeds.Values.Any(v => !double.IsFinite(v) || v is < 0.1 or > 5)) throw new IOException("EffortSpeeds 范围为 0.1–5");
        return s;
    }
    internal bool IsCodexRunning()
    {
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(name));
            try { if (processes.Length > 0) return true; }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        return false;
    }
}

internal sealed record GProfile(int Brightness, int Mode, int Interval, bool BatteryOff, bool LidOff, string? Custom)
{
    internal bool Streaming => Mode is 19 or 20 or 22;
    internal static GProfile Read()
    {
        using var stream = new FileStream(Files.GHelper, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        int Get(string key, int fallback = 0) => root.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;
        return new(Math.Clamp(Get("matrix_brightness"), 0, 3), Get("matrix_running"), Math.Clamp(Get("matrix_interval"), 0, 255),
            Get("matrix_auto") == 1, Get("matrix_lid") == 1,
            root.TryGetProperty("slash_custom", out var c) ? c.GetString() : null);
    }
    internal static readonly byte[] ModeCodes = [0x10, 0x12, 0x13, 0x1D, 0x1A, 0x19, 0x25, 0x24, 0x26, 0x32, 0x33, 0x34, 0x42, 0x43, 0x44, 0x06, 0x60, 0x61, 0x62];
    internal void Apply(SlashHid device, byte[] pool)
    {
        if (Streaming) return; // G-Helper owns the streaming modes and has already applied them.
        device.Send(0xD7, 0, 0, 1, 0xAC);
        device.Send(0xD2, 2, 1, 8, 0xAB);
        if (Mode == 15 && !string.IsNullOrWhiteSpace(Custom))
        {
            var bytes = Convert.FromHexString(Custom.Replace("-", "").Replace(" ", ""));
            if (bytes.Length != 7) throw new IOException("G-Helper slash_custom 必须是 7 字节");
            device.BeginCustom(); device.Frame(bytes);
        }
        else
        {
            if (Mode != 21)
            {
                if (Mode < 0 || Mode >= ModeCodes.Length) throw new IOException($"不支持的 G-Helper 灯效 {Mode}");
                var modes = (byte[])pool.Clone(); modes[1] = 0xD3; modes[2] = 4; modes[6] = ModeCodes[Mode]; device.SendRaw(modes);
            }
            device.Send(0xD3, 3, 1, 8, 0xAB, 0xFF, 1, Mode == 21 ? (byte)0 : (byte)1, 6, (byte)(Brightness * 85.333), 0xFF, (byte)Interval);
            device.Send(0xD4, 0, 0, 1, 0xAB);
        }
        device.Send(0xD8, 2, 0, 1, Brightness == 0 ? (byte)0x80 : (byte)0);
    }
}

internal sealed record Snapshot(string Device, GProfile Profile, byte[] Pool, byte[] Builtin, byte[] Custom)
{
    internal static Snapshot Capture(SlashHid device, GProfile profile)
    {
        var pool = device.Query(0xD2, 3, 0, 0x0C);
        var builtin = device.Query(0xD2, 2, 1, 8, 0xAB);
        var custom = device.Query(0xD2, 2, 1, 8, 0xAC);
        if (pool[0] != 0x5E || pool[5] != 1 || builtin[5] != 0xAB || custom[5] != 0xAC)
            throw new IOException("控制器返回了无法识别的配置，未接管灯条。");
        return new(device.Path, profile, pool, builtin, custom);
    }
    internal void Restore(SlashHid device, GProfile? current)
    {
        var profile = current ?? Profile;
        if (profile.Streaming) return;
        if (current is not null && current != Profile)
        {
            profile.Apply(device, Pool); return;
        }
        // Restore the exact AB/AC records and animation slots captured before takeover.
        device.Send(0xD7, 0, 0, 1, 0xAC);
        foreach (var original in new[] { Custom, Builtin })
        {
            var b = (byte[])original.Clone(); b[1] = 0xD3; b[2] = 3; device.SendRaw(b);
            device.Send(0xD4, 0, 0, 1, b[5]);
        }
        var modes = (byte[])Pool.Clone(); modes[1] = 0xD3; modes[2] = 4; device.SendRaw(modes);
        device.Send(0xD4, 0, 0, 1, 0xAB);
        if (profile.Mode == 15 && !string.IsNullOrWhiteSpace(profile.Custom)) profile.Apply(device, Pool);
        else device.Send(0xD8, 2, 0, 1, profile.Brightness == 0 ? (byte)0x80 : (byte)0);
    }
    internal static bool Recover()
    {
        if (!File.Exists(Files.Journal)) return true;
        try
        {
            var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Files.Journal), Files.Json) ?? throw new IOException("恢复记录为空");
            using var d = SlashHid.Open(s.Device);
            GProfile? current = null;
            try { current = GProfile.Read(); } catch { }
            s.Restore(d, current);
            File.Delete(Files.Journal); Files.Log("原有灯效已恢复"); return true;
        }
        catch (Exception ex) { Files.Log("恢复失败，将保留记录重试：" + ex.Message); return false; }
    }
}

internal sealed class Telemetry : IDisposable
{
    private long idle, total;
    private IntPtr query, gpuCounter;
    internal string GpuStatus { get; private set; } = "未启用";
    internal Telemetry(bool includeGpu)
    {
        if (!Native.GetSystemTimes(out idle, out var k, out var u)) throw new IOException("无法读取 CPU 负荷");
        total = k + u;
        if (includeGpu)
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) == 0 &&
                Native.PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuCounter) == 0)
            { Native.PdhCollectQueryData(query); GpuStatus = "采样中"; }
            else { GpuStatus = "不可用，仅使用 CPU"; if (query != IntPtr.Zero) Native.PdhCloseQuery(query); query = IntPtr.Zero; }
        }
    }
    internal (double Cpu, double? Gpu) Sample()
    {
        if (!Native.GetSystemTimes(out var i, out var k, out var u)) throw new IOException("CPU 采样失败");
        long t = k + u, delta = t - total;
        double cpu = delta > 0 ? 100d * (delta - (i - idle)) / delta : 0;
        idle = i; total = t;
        double? gpu = null;
        if (query != IntPtr.Zero && Native.PdhCollectQueryData(query) == 0)
        {
            uint bytes = 0;
            var status = Native.PdhGetFormattedCounterArray(gpuCounter, 0x8200, ref bytes, out _, IntPtr.Zero);
            if (status == 0x800007D2 && bytes > 0 && bytes < 16_000_000)
            {
                var buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (Native.PdhGetFormattedCounterArray(gpuCounter, 0x8200, ref bytes, out var count, buffer) == 0)
                    {
                        var engines = new Dictionary<string, double>();
                        for (int n = 0; n < count; n++)
                        {
                            var item = Marshal.PtrToStructure<Native.CounterItem>(buffer + n * Marshal.SizeOf<Native.CounterItem>());
                            if (item.Value.Status > 1 || !double.IsFinite(item.Value.Value)) continue;
                            var name = Marshal.PtrToStringUni(item.Name) ?? "";
                            // Each instance is one PID on one physical GPU engine. Sum PIDs per engine, then take the busiest engine.
                            var key = Regex.Replace(name, @"^pid_\d+_", "");
                            engines[key] = engines.GetValueOrDefault(key) + Math.Max(0, item.Value.Value);
                        }
                        if (engines.Count > 0) { gpu = Math.Clamp(engines.Values.Max(), 0, 100); GpuStatus = "可用"; }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (gpu is null) GpuStatus = "无有效计数，仅使用 CPU";
        }
        return (Math.Clamp(cpu, 0, 100), gpu);
    }
    public void Dispose() { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); query = IntPtr.Zero; }
}

internal static class Pattern
{
    internal static byte[] Render(double load, double seconds, double cap)
    {
        // A softly breathing marker remains visible even while cloud inference uses little local CPU.
        load = Math.Clamp(load, 0, 100); cap = Math.Clamp(cap, 0, 1);
        double pulse = 0.72 + 0.28 * (0.5 + 0.5 * Math.Sin(seconds * (1.2 + load * 0.045)));
        double fill = Math.Max(0.85, load / 100 * 7);
        var b = new byte[7];
        for (int n = 0; n < 7; n++)
            b[6 - n] = (byte)Math.Clamp(Math.Round(255 * cap * pulse * Math.Clamp(fill - n, 0, 1)), 0, 255);
        return b;
    }
    internal static string? BlockReason(bool paused, bool suspended, bool locked, bool running, GProfile p, Settings s, bool battery)
    {
        if (paused) return "已暂停，G-Helper 控制灯条";
        if (suspended) return "系统休眠，已释放灯条";
        if (locked) return "屏幕已锁定，已释放灯条";
        if (!running) return "等待 Codex 启动";
        if (p.Streaming) return "G-Helper 正在使用音频/电量灯效，请切换普通灯效";
        if (p.Mode < 0 || (p.Mode > 18 && p.Mode != 21)) return "G-Helper 灯效尚不支持，已让出控制";
        if (s.RespectGHelperOff && (p.Brightness == 0 || p.Mode == 21)) return "遵循 G-Helper 的关闭灯条设置";
        if (p.BatteryOff && battery) return "遵循 G-Helper 的电池关灯设置";
        return null;
    }
}
