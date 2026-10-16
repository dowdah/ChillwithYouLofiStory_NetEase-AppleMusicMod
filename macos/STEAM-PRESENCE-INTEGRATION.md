# Steam 游戏信息状态集成（1.6.0.0 候选）

2026-10-16：用户明确接受仅在好友的“查看游戏信息 → 状态”显示，解除原计划的好友列表主行门槛，授权继续真实音乐和计时器集成。仍须最终用户验收后才能 Push 与创建 PR；不合并、不删除分支。

## 实现边界

- 同一个 MusicBridge DLL，只向 AppID 3548580 写入 `status`；不使用任何模板，不写 `steam_display`、connect，不调用 Init/Shutdown/ClearRichPresence，不创建 Steam 回调循环。
- 入口为 Mod 设置 → 系统 → Steam 好友状态。主设置草稿须先保存或取消；子面板有独立草稿、保存、取消、恢复默认、预览与重试，不与主配置混合提交。
- 独立 `config/steam-presence.options.json`，schema 1；主配置仍为 schema 2。默认关闭、显示音乐和计时器开关默认开、曲名默认不公开。损坏或未来配置保留原文件，关闭功能并禁用保存；手动修复后重新打开子面板可重读。不自动覆盖。
- Unity 主线程每秒调用现有 `PlaybackSnapshotService.Capture()`，使用实际活动音源而非浏览中的音源页面。区分 Idle/Loading/Playing/Paused/Failed；只在 Playing/Paused 且曲名隐私开关允许时加入标题。缓冲不宣称正在播放。
- 文本优先级：计时器、音乐状态、可选曲名；255 UTF-8 字节上限，优先截断曲名，清除控制字符并保持代理对完整。无可展示内容则释放 status。
- 正常发布最小间隔 5 秒、变化才提交、连续变化合并最新值；断线重连重新发布，失败按 5/15/30/60 秒退避。计数和最近成功仅指 API，不自动宣称好友已看到。
- 保存原 status 及最后写入值；禁用立即尝试有条件恢复，离线恢复后续重试。原游戏/其他模块接管 status 或出现 steam_display 时停止竞争，等待手动重试；退出只尝试恢复并禁止后续发布。不会清除别的 Presence 键。
- 预览只在设置界面，富文本解析关闭。日志与诊断导出不包含真实曲名、账号、曲目 URL、私人路径。macOS 系统媒体控制与该功能独立。

## 计时器证据与读取方式

游戏 `Assembly-CSharp.dll` SHA-256：`32e6c68ec92a8e8e905a2fe23376c7a69b65563d2ba473de5f1c1fba08054f2d`。使用执行无关的 Cecil 检查器 `macos/tools/timer-inspect` 定向读出字段、签名及 CIL；REA 在此前同一程序集的元数据预算限制仍保留，不把普通检查器输出标为 REA 完整语义分析。

| 对象 | 静态核对 | 实现 |
| --- | --- | --- |
| PomodoroService | `_mainState` 为 Idle/Work/Rest/Pause；`_currentPomodoroType` 为 Work/Break/Complete | 缓存经类型、枚举值和签名校验的字段 |
| PomodoroTimerService | `RemainTimeSpan()` 为 max(总时长−已过,0)；`CurrentTimeSpan()` 为阶段总时长；`_isRunning` 控制递增 | 剩余分钟向上取整；已过=总时长−剩余，不从界面文字推算 |
| CountupService | Idle/Work/Pause；Pause 将阶段置为 Break，UnPause 置回 Work | 正计时 Break+Pause 是暂停，不误判不可用 |
| CountupTimerService | `get_ElapsedTimeSeconds()` 返回已有计时值；`_isRunning` 控制递增 | 已过分钟向下取整，不做墙钟外推 |
| 生命周期 | 两个服务均有无参 void Setup/Update/Dispose；Facility 的更新调用对应服务 Update | 仅 Harmony Postfix 观察现有实例与销毁；弱引用、心跳失效、场景切换/卸载清空后重新观察 |

不调用任何服务构造、容器 Resolve 或计时控制方法。两个计时器同时活动时判为归属不明，省略计时器而保留音乐。读取失败或签名改变也只使计时器不可用。

从同一实例的活动状态进入 Idle 显示“计时完成”约 10 秒；下一阶段立即优先。游戏的重置/停止也会进入完成流程，因此同样显示完成提示。未启动时不显示；实例失效会清除提示。每秒采样可能遗漏不足一秒的完整启停，不承诺逐秒同步。

**上述是静态核对与实现设计，真实 Mono 挂钩、计时器阶段/暂停/秒数仍待本次候选验收。**

## 自动验证

- presence-core 45、presence-publisher 114、presence-settings 41、presence-timer 65 项断言通过；使用模拟 Steam/游戏，不向好友发布。
- presence-lifecycle 32 项通过，覆盖无界面采样、源读取异常、场景失效/重绑、隐私、禁用后恢复及退出幂等；包括禁用时异常隔离与恢复重试。
- shuffle-core / lyrics-core / media-core 通过；Python 启动切换与恢复相关 13 项测试通过。
- 托管 Release 构建通过（24 条既有警告）。
- 完整 Bridge.Tests 此次连续两轮各 1,099 条 PASS，其中前轮探针调查时出现两次超时的 FLAC 取消释放测试本次均通过。三个位置的 FLAC 原生库相同，相关测试及实现源码未改变；先前超时的根因未定位，不称其已修复。
- 不沿用历史版本的实机通过或豁免作为本次新功能验收。

## 候选与验收

首个候选已于 2026-10-16 打包并通过 arm64 启动预检：

- 源码提交：`67d54dd135693876957981ccc8ee63d3a661f68c`（实现阶段提交 `6fb31e8`、`446db5a`、`f563e77`；后续为测试/清单/文档）。
- 目录：`macos/dist/SteamPresenceCandidate-67d54dd`。
- DLL SHA-256：`ab8381d0ed6357a7befa09ae7a637a4e55e57c7f3222935bd6f8d0a3e5cce65d`。
- 核心库摘要：`9d5743f020979d255275bc5728bbc8603e6f62a6cfb15f42e3b1ecc94b73cd1b`；完整库/启动器/游戏程序集哈希在候选 `release-manifest.json`。
- 打包时源码工作区干净；Presence 配置从公开默认样例复制，Enabled=false、ShareTrackTitle=false。主配置与窗口布局从旧候选复制，未复制 cache/data/logs。
- 确认 Mac Steam 与游戏退出后已切换本游戏的启动选项；旧目标为 `ExperienceLayoutCandidate/launch-core.sh`。独立恢复记录 `.local/steam-presence-candidate-67d54dd/switch.json` 和同名 VDF 备份均保留。本文的候选记录是后续纯文档更新，不改变受测实现。

如候选启动失败，先正常退出 Mac 游戏和 Steam，再运行：

```sh
python3 macos/tools/steam_candidate_options.py restore --record .local/steam-presence-candidate-67d54dd/switch.json
```

使用 `macos/dist/SteamPresenceCandidate-<commit>`，固定复用 ExperienceLayoutCandidate 已验证运行库及原生库，独立清单记录源码、主配置/Presence schema、游戏程序集和库哈希。仅复制必要配置；不复制旧缓存/日志/运行数据，不使用硬编码 ChillMusicMac 的升级工具。原目录保留，切换使用独立备份记录及原值冲突校验。

本次真实验收尚未开始，以下均 Pending：

1. 默认关闭时的只读诊断：番茄钟工作/休息/暂停/完成、正计时开始/暂停/停止与游戏秒数一致；场景实例失效后重新绑定。
2. 网易云队列/FM、Apple Music、游戏自带/导入音乐换歌与暂停；浏览其他音源页面不切换实际显示；缓冲、快速切歌和剧情让路不残留旧曲名。
3. 启用、禁用、公开曲名、保存/取消/默认与重启持久化；主设置未保存拦截，正常/窄窗口控件可达；原播放、双悬浮窗与系统媒体控制无新增回归。
4. 独立好友账号观察代表性音乐/计时器文本变化与关闭/退出清理。仅“游戏信息”详情是验收目标；不要求主好友列表显示。
5. 同一哈希候选临时切回旧目标并确认旧版启动，再切回新候选；最终验收通过后保持新目标。

60 分钟长稳及 Intel 实机按原约定为 NotRun，不扩充交付门槛。任何功能代码修复须新候选并重验受影响场景；纯文档提交不改变受测 DLL，记录其对应实现提交。

最终用户确认通过前不 Push、不 PR。PR 面向 origin/main，仓库 `dowdah/ChillwithYouLofiStory_NetEase-AppleMusicMod`；创建后关联当前任务，检查远端 CI，不自动合并。
