# v1.0.0 — Initial public preview / 首个公开预览版

Codex Slash turns the built-in seven-segment Slash Lighting bar into a Codex task progress indicator, with model-specific animation styles and reasoning-effort-specific cadence.

## Features / 功能

- Progress length: 10% → 1 segment; 20% → 2; 100% → 7.
- Completed regions remain lit with a circulating brightness highlight.
- Six configurable animation styles; configurable reasoning-effort speeds.
- Local Codex model/effort/lifecycle detection, structured-plan progress and explicit milestone reporting.
- Optional CPU/GPU modulation affects speed only.
- G-Helper coexistence, previous-lighting restoration and watchdog recovery.
- Windows tray interface and optional login startup.

灯条长度表示任务完成率；已完成区域保持常亮，高光在内部循环。模型决定样式，推理强度决定节奏。缺少进度数据时显示未知，不按运行时间或电脑负荷猜测完成率。

## Requirements / 使用条件

- Windows x64 and .NET 10 Windows Desktop Runtime (not bundled).
- Dedicated ASUS 0B05:193B / COL04 / report 0x5E / seven-segment controller.
- Tested: ROG Zephyrus G14 GA403UV and G-Helper 0.285.
- Extract the ZIP and run CodexSlash.exe; keep settings.json beside it.

## Preview limitations / 预览版限制

- No 35-segment, AniMe Matrix or PID 19B6 support.
- Codex local JSONL changes may require adapter updates; intermediate percentages require structured plans or explicit reports.
- Audio/battery streaming effects in G-Helper retain ownership.
- Hardware validation checked HID writes and configuration readback, not visual brightness. Other models, physical lid-close and full power loss are untested.
- Unsigned executable; .NET runtime required.

## Validation / 验证

34 automated checks passed. On-device animation writes, byte-for-byte controller configuration restoration, forced-process-termination watchdog recovery and model/effort/progress integration were tested. See TEST-RESULTS.md for boundaries.

GPL-3.0. Protocol attribution to G-Helper is included in THIRD-PARTY-NOTICES.md.
