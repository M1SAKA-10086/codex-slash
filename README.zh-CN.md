# Codex Slash

为 ASUS Slash Lighting 增加 Codex 任务进度灯效，可与 G-Helper 同时运行。

## 启动

双击同目录的 **CodexSlash.exe**。需要先安装 .NET 10 Windows Desktop Runtime。程序不要求管理员权限。

关闭窗口后驻留系统托盘；右键托盘可暂停、继续、打开设置或“退出并恢复 G-Helper”。登录自启需要在托盘菜单中自行勾选，默认关闭。请保留整个文件夹，特别是 `settings.json`。

## 灯效

灯条共有 7 个可独立控制的段。已完成部分保持常亮，高光只在已亮区域内循环。

| 完成率 | 亮起段数 |
| --- | --- |
| 0% | 0 |
| 10% | 1 |
| 20% | 2 |
| 30% / 40% | 3 |
| 50% | 4 |
| 60% / 70% | 5 |
| 80% | 6 |
| 90% / 100% | 7 |

映射公式为 `ceil(完成率 × 7 / 100)`。硬件只能提供 7 段，无法使每一个百分点都对应一个独立格子。

默认样式：

| 模型 | 灯效 |
| --- | --- |
| gpt-6.1-sol | 正向循环高光 orbit |
| gpt-6-sol | 连续波形 wave |
| gpt-6-astra | 双高光循环 twin |
| gpt-6.1-terra | 反向循环 reverse |
| gpt-5.6-terra | 往返扫动 bounce |
| gpt-6-luna | 整段呼吸 pulse |

`low → medium → high → xhigh → max → ultra` 对应逐渐更快的基础节奏。模型和推理强度读取实际会话元数据，不根据 CPU 使用率猜测。新模型按家族或稳定规则选择默认样式，可以在 `ModelEffects` 中指定完整模型名称覆盖。

原需求中的负荷响应仍保留：本机 CPU/GPU 负荷会在基础节奏上增加最多 40% 的流速，不改变完成率或亮起段数。`ModulateSpeedWithLoad: false` 可关闭它。

## 进度从哪里来

程序只读本地 `%USERPROFILE%\.codex\sessions`（也支持 `CODEX_HOME`），跟踪任务开始、结束、模型、推理强度和计划更新。

1. 有结构化计划更新：按“已完成步骤数 / 总步骤数”计算进度。这是步骤完成率，不是剩余时间预测。
2. 任务直接上报百分比：优先采用上报值，且绑定线程和当前轮次，旧任务的百分比不会泄漏到新任务。
3. 没有进度数据：显示“进度未知”，使用单段待机呼吸；不会根据运行时间、工具次数、Token 或 CPU 负荷伪造完成率。
4. 收到成功完成事件：显示 100% 约 12 秒，再回到待机。中断不显示成功。

Codex 并非每次任务都会写入结构化计划。当前桌面版本的本地 JSONL 格式属于实现细节，未来版本改变格式时可能需要更新适配器。程序不连接或重启 Codex 的 app-server，也不修改 Codex 的配置或已有 hooks。

多任务同时运行时显示最近有活动的主任务，排除子代理日志。可在 `settings.json` 中设置 `ThreadId` 锁定一个线程。首次启动扫描最近 24 份会话，已经跟踪的活动会话会继续保留；超过 10 分钟无新事件的任务按待机处理。

### 让没有结构化计划的任务上报进度

在任务开始时给 Codex 说明：“按可验收的里程碑维护进度，每完成一项，用 CodexSlash.exe --progress N 上报，不按时间或工具调用次数估算。”

在 PowerShell 中执行：

```powershell
& '完整路径\CodexSlash.exe' --progress 40
```

命令自动寻找当前任务。如果有多个聊天，建议传入明确的线程、轮次：

```powershell
& '完整路径\CodexSlash.exe' --progress 40 --thread '线程UUID' --turn '当前轮次ID'
```

它只写入 `%LOCALAPPDATA%\CodexSlash\progress\线程UUID.json`。这是一条本地进度上报命令，不会发送消息或请求模型。可参照 [INTEGRATION.md](INTEGRATION.md) 为自己的项目添加说明。

## G-Helper 兼容行为

- 不写入 G-Helper 的 `config.json`，不修改性能模式、风扇、充电、键盘灯或固件。
- 默认遵循 G-Helper 的灯条关闭和电池关灯设置，不更改硬件的合盖、电池、启动或休眠动画开关。
- 检测到 G-Helper 音频、电量等持续输出模式时让出灯条，状态窗口会提示切换普通灯效。普通固件灯效（例如 Spectrum）可以直接使用。
- Codex 进程退出、暂停、程序退出、锁屏或休眠时恢复原灯效；运行期间修改 G-Helper 灯效时会同步新设置。
- 接管前保存控制器配置。独立恢复进程会在主程序崩溃或被终止后恢复；如设备当时不可用，下次启动仍会重试。断电时恢复进程无法工作，重新启动程序可读取保留的恢复记录。

本版仅打开 `ASUS 0B05:193B / COL04 / 128 字节 / Report 0x5E` 的专用 Slash 接口，已在 **ROG Zephyrus G14 GA403UV** 实测。35 段灯条、AniMe Matrix 和使用键盘复用接口 PID 19B6 的新机型不在本版支持范围。

## 设置

编辑 `settings.json` 后，在托盘菜单点“重载设置”。

- `BrightnessLimit`：最大亮度，0.05–1；还会受 G-Helper 亮度限制。
- `ModelEffects`：完整模型名 → orbit / reverse / bounce / twin / wave / pulse。
- `EffortSpeeds`：推理强度 → 每秒基础循环次数，0.1–5。
- `ModulateSpeedWithLoad`：是否用本机负荷微调流速。
- `IncludeGpu`：是否读取 Windows GPU Engine 计数。不可用时自动使用 CPU，不唤醒独显运行 CUDA。
- `SampleMilliseconds`：进程、配置和负荷采样周期，500–10000 ms；动画独立以约 8 帧/秒刷新。
- `ProcessNames`：识别的主进程名称，默认 `Codex`。
- `RespectGHelperOff`：默认 true，遵循 G-Helper 关灯设置。
- `ThreadId`：默认 null；可填完整 Codex 线程 UUID。
- `CodexHome`：可选，指定 Codex 配置目录；不设置时使用环境变量 `CODEX_HOME` 或用户目录下 `.codex`。

## 故障排查与构建

运行日志、恢复记录和实时状态在 `%LOCALAPPDATA%\CodexSlash`。程序只保存模型/进度等状态，不保存或发送聊天正文。

```powershell
.\CodexSlash.exe --diagnose '诊断文件完整路径.json'  # 不写入硬件
.\CodexSlash.exe --restore                         # 尝试恢复遗留的灯效记录
.\CodexSlash.exe --self-test '测试结果完整路径.json'
```

手工调用上面的恢复命令前，应先从托盘退出主程序。命令行程序使用 Windows GUI 子系统，若需要等待执行完成，可用 `Start-Process -Wait -PassThru`。

源码在 `src/`，使用 .NET 10 SDK，运行 `build.ps1` 可重新生成程序，无第三方 NuGet 依赖。发布文件依赖 .NET 10 Windows Desktop Runtime；不是自带运行时的独立安装包。

协议参考：[G-Helper SlashDevice.cs](https://github.com/seerge/g-helper/blob/0a1538ac4fb0337f6cf3d64a907acde10c91a3d9/app/AnimeMatrix/SlashDevice.cs)。Codex 计划和轮次事件参考：[官方 App Server 文档](https://learn.chatgpt.com/docs/app-server)。

本项目按 GNU GPL v3 发布，详见 LICENSE 和 THIRD-PARTY-NOTICES.md。

