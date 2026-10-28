using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexSlash;

internal static class Program
{
    internal const string MutexName = @"Local\CodexSlash.193B.v1";
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--watchdog") return Watchdog(args);
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.ElementAtOrDefault(1));
            if (args.Length > 0 && args[0] == "--diagnose") return Diagnose(args.ElementAtOrDefault(1));
            if (args.Length > 0 && args[0] == "--progress") return ReportProgress(args);
            if (args.Length > 0 && args[0] == "--render-preview")
            {
                ApplicationConfiguration.Initialize();
                using var preview = new Dashboard(Settings.Load(), false);
                preview.RenderPreview(args.ElementAtOrDefault(1) ?? System.IO.Path.Combine(Files.Data, "preview.png")); return 0;
            }
            using var mutex = new Mutex(false, MutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                MessageBox.Show("Codex Slash 已在运行，请从系统托盘打开。", "Codex Slash"); return 0;
            }
            try
            {
                if (!Snapshot.Recover()) throw new IOException("上次的灯效尚未恢复。请检查设备连接，恢复记录已保留。");
                if (args.Length > 0 && args[0] == "--restore") return 0;
                if (args.Length > 0 && args[0] == "--hardware-test") return HardwareTest(args.ElementAtOrDefault(1));
                if (args.Length > 0 && args[0] == "--crash-test") return CrashTest();
                var settings = Settings.Load();
                var me = Process.GetCurrentProcess();
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    ArgumentList = { "--watchdog", me.Id.ToString(), me.StartTime.ToUniversalTime().Ticks.ToString() },
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                });
                ApplicationConfiguration.Initialize();
                using var form = new Dashboard(settings, args.Contains("--tray"));
                Application.Run(form);
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Files.Log(ex.ToString());
            if (args.Length > 1 && args[0].EndsWith("test")) Files.Save(args[1], new { ok = false, error = ex.ToString() });
            else MessageBox.Show(ex.Message + "\n日志：" + Files.Data, "Codex Slash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int ReportProgress(string[] args)
    {
        if (args.Length < 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
            throw new ArgumentException("用法：CodexSlash.exe --progress 40 [--thread UUID --turn TURN_ID]");
        string? Get(string key) { int i = Array.IndexOf(args, key); return i >= 0 ? args.ElementAtOrDefault(i + 1) : null; }
        string? thread = Get("--thread") ?? Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        string? turn = Get("--turn");
        if (thread is null || turn is null)
        {
            var s = Settings.Load();
            if (thread is not null) s = s with { ThreadId = thread };
            using var provider = new CodexProgress(s);
            TaskState? task = null;
            var until = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < until)
            {
                task = provider.Current;
                if (task is { Active: true }) break;
                Thread.Sleep(150);
            }
            if (task is not { Active: true }) throw new IOException("未找到运行中的任务，请显式提供 --thread 和 --turn。");
            thread ??= task.Thread; turn ??= task.Turn;
        }
        CodexProgress.Report(thread, turn, percent); return 0;
    }

    private static int Watchdog(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks)) return 2;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.StartTime.ToUniversalTime().Ticks == ticks) p.WaitForExit();
        }
        catch (ArgumentException) { }
        // A new instance takes responsibility for recovery if it started first.
        using var mutex = new Mutex(false, MutexName);
        bool held;
        try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) return 0;
        try
        {
            for (int n = 0; n < 5; n++)
            {
                if (Snapshot.Recover()) return 0;
                Thread.Sleep(1000);
            }
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static int Diagnose(string? output)
    {
        object result;
        try
        {
            using var h = SlashHid.Open();
            using var telemetry = new Telemetry(true);
            Thread.Sleep(1100);
            var sample = telemetry.Sample();
            result = new { ok = true, device = h.Path, gHelper = GProfile.Read(), codexRunning = Settings.Load().IsCodexRunning(), cpu = sample.Cpu, gpu = sample.Gpu, gpuStatus = telemetry.GpuStatus, dataDirectory = Files.Data, protocol = "0B05:193B COL04 / report 5E / 7 segments", hardwareWrites = false };
        }
        catch (Exception ex) { result = new { ok = false, error = ex.ToString() }; }
        Files.Save(output ?? System.IO.Path.Combine(Files.Data, "diagnostics.json"), result);
        return result.GetType().GetProperty("ok")?.GetValue(result) is true ? 0 : 1;
    }

    private static int HardwareTest(string? output)
    {
        using var h = SlashHid.Open();
        var profile = GProfile.Read();
        if (profile.Streaming) throw new IOException("测试前请将 G-Helper 切换到普通灯效");
        var before = Snapshot.Capture(h, profile);
        Files.Save(Files.Journal, before);
        bool restored = false;
        try
        {
            h.BeginCustom();
            for (int n = 0; n < 24; n++) { h.Frame(ProgressPattern.Render(n * 100d / 23, "gpt-6.1-sol", "high", n / 12d, 0.6, 0, Settings.Load())); Thread.Sleep(80); }
        }
        finally { restored = Snapshot.Recover(); }
        var after = Snapshot.Capture(h, GProfile.Read());
        var ok = restored && before.Pool.SequenceEqual(after.Pool) && before.Builtin.SequenceEqual(after.Builtin) && before.Custom.SequenceEqual(after.Custom);
        Files.Save(output ?? System.IO.Path.Combine(Files.Data, "hardware-test.json"), new
        {
            ok, restored, poolMatches = before.Pool.SequenceEqual(after.Pool), builtinMatches = before.Builtin.SequenceEqual(after.Builtin), customMatches = before.Custom.SequenceEqual(after.Custom),
            beforePool = Convert.ToHexString(before.Pool), afterPool = Convert.ToHexString(after.Pool),
            beforeBuiltin = Convert.ToHexString(before.Builtin), afterBuiltin = Convert.ToHexString(after.Builtin),
            beforeCustom = Convert.ToHexString(before.Custom), afterCustom = Convert.ToHexString(after.Custom)
        });
        return ok ? 0 : 1;
    }

    private static int CrashTest()
    {
        using var h = SlashHid.Open();
        var me = Process.GetCurrentProcess();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList = { "--watchdog", me.Id.ToString(), me.StartTime.ToUniversalTime().Ticks.ToString() },
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        });
        Files.Save(Files.Journal, Snapshot.Capture(h, GProfile.Read()));
        h.BeginCustom(); h.Frame(Pattern.Render(70, 0, 0.5));
        // Test harness terminates this process after seeing the recovery journal.
        Thread.Sleep(30000); return 0;
    }
}

internal sealed class Dashboard : Form
{
    private Settings settings;
    private Telemetry telemetry;
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 125 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Label status = new(), numbers = new(), detail = new();
    private readonly Label progressText = new();
    private CodexProgress progress;
    private TaskState? task;
    private double? taskPercent;
    private readonly Panel strip = new();
    private readonly Button pause = new();
    private readonly ToolStripMenuItem pauseMenu;
    private SlashHid? device;
    private Snapshot? snapshot;
    private GProfile? profile;
    private double load;
    private double nextSample, nextRetry, nextInit;
    private bool paused, suspended, locked, quitting, released;
    private string state = "正在检查设备…";
    private byte[] preview = new byte[7];

    internal Dashboard(Settings settings, bool startHidden)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        this.settings = settings; telemetry = new(settings.IncludeGpu); progress = new(settings);
        Text = "Codex Slash · 任务进度灯条";
        ClientSize = new Size(590, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 25, 31); ForeColor = Color.FromArgb(232, 239, 242);
        Font = new Font("Microsoft YaHei UI", 10);
        AddLabel("CODEX  /  SLASH", 28, 23, 530, 40, 21, Color.FromArgb(138, 238, 205));
        AddLabel("长度 = 任务进度    ·    流动 = 模型 × 推理强度", 30, 72, 530, 25, 10, Color.Silver);
        status.SetBounds(30, 121, 535, 43); status.Font = new Font(Font, FontStyle.Bold); Controls.Add(status);
        progressText.SetBounds(30, 167, 530, 28); Controls.Add(progressText);
        strip.SetBounds(30, 205, 530, 60); strip.Paint += PaintStrip; Controls.Add(strip);
        numbers.SetBounds(30, 285, 530, 25); Controls.Add(numbers);
        detail.SetBounds(30, 323, 530, 63); detail.ForeColor = Color.Silver; detail.Font = new Font(Font.FontFamily, 9); Controls.Add(detail);
        pause.Text = "暂停并恢复"; pause.SetBounds(30, 406, 150, 36); pause.Click += (_, _) => TogglePause(); StyleButton(pause); Controls.Add(pause);
        var reload = new Button { Text = "设置 / 重载", Bounds = new Rectangle(192, 406, 150, 36) };
        StyleButton(reload); Controls.Add(reload);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开状态窗口", null, (_, _) => ShowDashboard());
        pauseMenu = new ToolStripMenuItem("暂停并恢复 G-Helper", null, (_, _) => TogglePause()); menu.Items.Add(pauseMenu);
        menu.Items.Add("编辑 settings.json", null, (_, _) => OpenFile(System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json")));
        menu.Items.Add("重载设置", null, (_, _) => ReloadSettings());
        var startup = new ToolStripMenuItem("登录后自动启动") { CheckOnClick = true, Checked = IsStartup() };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked); menu.Items.Add(startup);
        menu.Items.Add("打开日志目录", null, (_, _) => OpenFile(Files.Data));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并恢复 G-Helper", null, (_, _) => Quit());
        var configMenu = new ContextMenuStrip();
        configMenu.Items.Add("编辑 settings.json", null, (_, _) => OpenFile(System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json")));
        configMenu.Items.Add("重载设置", null, (_, _) => ReloadSettings());
        reload.Click += (_, _) => configMenu.Show(reload, new Point(0, reload.Height));
        var exit = new Button { Text = "退出并恢复", Bounds = new Rectangle(354, 406, 150, 36) };
        StyleButton(exit); exit.Click += (_, _) => Quit(); Controls.Add(exit);
        trayIcon = MakeIcon(); Icon = trayIcon;
        tray = new NotifyIcon { Icon = trayIcon, Text = "Codex Slash", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowDashboard();
        FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.SessionSwitch += SessionChanged;
        timer.Tick += Tick; timer.Start();
        Shown += (_, _) => { if (startHidden) Hide(); };
        ResumeLayout(false);
        Files.Log("程序启动");
    }
    private void AddLabel(string text, int x, int y, int w, int h, float size, Color color)
        => Controls.Add(new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = new Font("Microsoft YaHei UI", size), ForeColor = color });
    private static void StyleButton(Button b)
    { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(64, 81, 88); b.ForeColor = Color.White; b.BackColor = Color.FromArgb(34, 44, 51); }
    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(22, 35, 38));
            using var pen = new Pen(Color.FromArgb(138, 238, 205), 5);
            g.DrawLine(pen, 8, 26, 24, 6);
        }
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }
    private void Tick(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        try
        {
            if (now >= nextSample)
            {
                nextSample = now + settings.SampleMilliseconds / 1000d;
                var p = GProfile.Read();
                var sample = telemetry.Sample();
                load += settings.Smoothing * (Math.Max(sample.Cpu, sample.Gpu ?? 0) - load);
                task = progress.Current;
                bool recentCompletion = task is { Completed: true } && task.Updated > DateTimeOffset.UtcNow.AddSeconds(-12);
                bool activeTask = task is { Active: true } && task.Updated > DateTimeOffset.UtcNow.AddMinutes(-10);
                taskPercent = activeTask || recentCompletion ? task?.Percent : null;
                progressText.Text = taskPercent.HasValue ? $"进度  {taskPercent:0.#}%  ·  {ProgressPattern.LitCount(taskPercent.Value)} / 7 段  ·  {task?.Source}"
                    : activeTask ? "进度未知 · 等待计划步骤或任务上报" : "等待任务 · 呼吸待机";
                numbers.Text = $"{task?.Model ?? "等待模型"}   /   {task?.Effort ?? "unknown"}   /   {ProgressPattern.Effect(task?.Model ?? "unknown", settings)}";
                detail.Text = $"CPU {sample.Cpu:0}% · GPU {(sample.Gpu.HasValue ? sample.Gpu.Value.ToString("0") + "%" : "—")} · 负荷仅微调流速\n{(progress.ActiveCount > 1 ? $"{progress.ActiveCount} 个活动任务，显示最近活动任务" : "自动读取本地 Codex 任务")} · 关闭窗口后驻留托盘\n{(progress.Error is not null ? progress.Error : "无计划时不估算百分比；支持 --progress 上报")}";
                string? block = Pattern.BlockReason(paused, suspended, locked, settings.IsCodexRunning(), p, settings,
                    SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline);
                if (profile is not null && p != profile)
                {
                    Release(p); nextRetry = now + 2; state = "G-Helper 设置已变更，正在同步…";
                }
                profile = p;
                if (block is not null) { Release(p); state = block; }
                else if (device is null && now >= nextRetry)
                {
                    if (File.Exists(Files.Journal) && !Snapshot.Recover()) throw new IOException("等待恢复上一次灯效");
                    device = SlashHid.Open(); snapshot = Snapshot.Capture(device, p);
                    Files.Save(Files.Journal, snapshot); // Persist recovery BEFORE changing any hardware state.
                    device.BeginCustom(); nextInit = now + 5;
                    Files.Log("已接管 Codex 灯效");
                    state = "Codex 正在运行 · 任务进度灯条已接管";
                }
                Files.Save(System.IO.Path.Combine(Files.Data, "status.json"), new { state, controlling = device is not null, task, percent = taskPercent, cpu = sample.Cpu, gpu = sample.Gpu, load, activeTasks = progress.ActiveCount, updated = DateTimeOffset.UtcNow });
            }
            if (device is not null && snapshot is not null && profile is not null)
            {
                double cap = Math.Min(settings.BrightnessLimit, profile.Brightness == 0 ? settings.BrightnessLimit : profile.Brightness / 3d);
                preview = ProgressPattern.Render(taskPercent, task?.Model ?? "unknown", task?.Effort ?? "unknown", now, cap, load, settings);
                // Reinitialize the custom playback after G-Helper handles resume or a power transition.
                if (now >= nextInit)
                {
                    var record = device.Query(0xD2, 2, 1, 8, 0xAC);
                    if (record[5] == 0xAC && record[8] != 1) device.BeginCustom();
                    nextInit = now + 5;
                }
                device.Frame(preview);
            }
            else preview = new byte[7];
        }
        catch (Exception ex)
        {
            Files.Log(ex.Message);
            try { Release(profile); } catch (Exception restoreError) { Files.Log(restoreError.Message); }
            state = "等待重试：" + ex.Message; nextRetry = now + 5; nextSample = now + 2;
        }
        status.Text = state; tray.Text = ("Codex Slash · " + state)[..Math.Min(63, ("Codex Slash · " + state).Length)]; strip.Invalidate();
    }
    private void Release(GProfile? current)
    {
        if (device is null) return;
        try
        {
            if (snapshot is not null)
            {
                snapshot.Restore(device, current); File.Delete(Files.Journal); Files.Log("已交还 G-Helper 灯效");
            }
        }
        finally { device.Dispose(); device = null; snapshot = null; }
    }
    private void PaintStrip(object? sender, PaintEventArgs e)
    {
        e.Graphics.ScaleTransform(strip.ClientSize.Width / 530f, strip.ClientSize.Height / 60f);
        for (int i = 0; i < 7; i++)
        {
            int v = preview[6 - i];
            using var brush = new SolidBrush(Color.FromArgb(35 + v * 100 / 255, 45 + v * 190 / 255, 50 + v * 150 / 255));
            e.Graphics.FillPolygon(brush, [new Point(i * 74 + 15, 50), new Point(i * 74 + 37, 8), new Point(i * 74 + 69, 8), new Point(i * 74 + 47, 50)]);
        }
    }
    private void TogglePause()
    {
        paused = !paused;
        if (paused) { try { Release(profile); } catch (Exception ex) { Files.Log(ex.Message); } }
        pause.Text = paused ? "继续监测" : "暂停并恢复";
        pauseMenu.Text = paused ? "继续监测" : "暂停并恢复 G-Helper"; nextSample = 0;
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            suspended = e.Mode == PowerModes.Suspend;
            try { Release(profile); } catch (Exception ex) { Files.Log(ex.Message); }
            nextRetry = clock.Elapsed.TotalSeconds + 3; nextSample = 0;
        });
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
            if (locked) { try { Release(profile); } catch (Exception ex) { Files.Log(ex.Message); } }
            nextSample = 0;
        });
    }
    private void ReloadSettings()
    {
        try { var fresh = Settings.Load(); Release(profile); telemetry.Dispose(); progress.Dispose(); settings = fresh; telemetry = new(settings.IncludeGpu); progress = new(settings); nextSample = 0; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "设置未生效"); }
    }
    private void ShowDashboard() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    internal void RenderPreview(string path)
    {
        timer.Stop(); status.Text = "灯效预览 · 任务进度模式";
        progressText.Text = "进度  40%  ·  3 / 7 段  ·  计划步骤（演示）";
        numbers.Text = "gpt-6.1-sol   /   high   /   orbit";
        detail.Text = "已完成部分常亮，亮度高光在其中循环\n模型决定流动样式，推理强度决定节奏\n无计划时显示进度未知，支持任务上报百分比";
        preview = ProgressPattern.Render(40, "gpt-6.1-sol", "high", 0.1, 0.85, 0, settings);
        Show(); Refresh();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); Hide();
    }
    private void Quit() { quitting = true; Close(); }
    private static void OpenFile(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private static bool IsStartup() { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue("CodexSlash") is string; }
    private static void SetStartup(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) k.SetValue("CodexSlash", $"\"{Environment.ProcessPath}\" --tray"); else k.DeleteValue("CodexSlash", false);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true; timer.Stop();
            SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
            try { Release(GProfile.Read()); } catch (Exception ex) { Files.Log(ex.Message); }
            telemetry.Dispose(); progress.Dispose(); timer.Dispose(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
