# v1.0.1 — Ambient animation without progress / 无进度流动灯效

## 更新内容

- 未设置任务进度时，整条 7 段灯带持续流动、呼吸闪动。
- 无进度动画继续随模型样式、推理强度和可选 CPU/GPU 负荷微调而变化。
- 界面明确显示“进度未知”，不会虚构完成率。
- 有进度时，仍按完成率控制常亮区域长度。
- 托盘、自启和 G-Helper 原灯效恢复功能保留。

## Changes

- Keep the full seven-segment bar flowing and breathing even without reported task progress.
- Ambient mode follows model style and reasoning-effort cadence; optional load modulation affects speed only.
- Unknown progress stays unknown. Reported progress still determines illuminated length.
- Tray behavior, optional login startup and G-Helper restoration are retained.

## Validation and requirements

38 local regression checks passed, including motion in all six styles with no reported progress. Runtime state was verified as ambient with a null percentage on GA403UV.

Windows x64, .NET 10 Windows Desktop Runtime, and a supported dedicated ASUS 0B05:193B / COL04 seven-segment controller are required. No 35-segment, AniMe Matrix or PID 19B6 support. Local Codex JSONL changes may require adapter updates. The executable is unsigned. GPL-3.0 with G-Helper protocol attribution.

Extract CodexSlash-1.0.1-win-x64.zip and keep settings.json beside CodexSlash.exe. Existing users should exit the old tray program before replacing the executable and retain their preferred settings.json. SHA256SUMS-1.0.1.txt contains checksums for both attached archives.
