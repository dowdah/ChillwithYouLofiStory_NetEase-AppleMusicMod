# 无模板 status 实验

2026-10-16，用户在全部语言模板查询为空后，另行授权调查纯 `status`；这不是原好友列表主行功能的自动降级，也不代表音乐/计时器集成已获验收。

**实测结论：纯 status 无需模板，可在独立好友账号的桌面 Steam“游戏信息 → 状态”显示中文动态文本；A/B 显示、自动更新、自动清除已验证。好友列表主行仍只显示游戏名。因此详情展示通路可行，但不满足原计划的主状态行门槛，完整功能开发、Push、PR 继续暂停。**

## 范围与恢复

沿用阶段一适配层，在游戏主线程手动写入固定文本：

- A：`MusicBridge 音乐测试 A · 正在播放：测试曲目`
- B：`MusicBridge 计时器测试 B · 专注中，剩余 24 分钟`

只写 `status`；存在 `steam_display` 时拒绝实验，不创建、删除或猜测模板。手动结束、120 秒超时或插件退出时有条件恢复原 status，保留外部接管值。不读取实际音乐或计时器，不记录账号标识和真实曲名。运行版本仍是 1.5.0.0 探针候选。

使用独立 `SteamStatusProbe-<commit>` 目录，复用已验证 ExperienceLayoutCandidate 的运行库及原生库，不覆盖旧运行目录。`steam_candidate_options.py` 要求显式旧/新启动器、配置路径和独立恢复记录；退出 Mac 游戏与 Steam 后才可切换。拒绝原值冲突，恢复只修改 AppID 3548580 的启动选项，不整体覆盖 VDF 备份。

## 当前客户端静态证据（不是好友端实测）

macOS Steam 随附 `steamui/sp.js` SHA-256：`5a28c25041d07e69cf591a414519e0cf970b6884c1a0106659dcd9f1fb9586f9`。只读取已安装代码，没有修改客户端。

| 字节位置（0 起始） | 观察 |
| --- | --- |
| 6077964 | `HasCurrentGameRichPresence` 仅检查 `steam_display` |
| 6078044 | `HasRichPresenceForViewGameInfo` 检查 status/connect/connect_private |
| 6078525 | `GetCurrentGameStatus` 先取本地化显示，后回退 status，再回退空文本 |
| 13019214 | `GameInfoDialog_Status` 使用 `GetCurrentGameStatus` 的返回值 |

有针对性的调用点检查发现，好友列表及悬停迷你资料使用 `GetCurrentGameRichPresence`，不使用上面的 status 回退。推断：纯 status 可出现在“查看游戏信息”，但不进入好友列表/悬停主状态。这与 [Steamworks SetRichPresence 文档](https://partner.steamgames.com/doc/api/ISteamFriends#SetRichPresence) 所述 status 用途一致；仍须独立好友端实测。

REA 分析尝试先受沙箱辅助程序编译限制；宿主执行进入 JavaScript 解析后仍返回分析失败。此处只采用原始代码的定向定位，不声称已取得完整语义图或穷尽所有客户端页面。

## 验证记录

- presence-core：45 项模拟断言通过（包括仅写 status、A/B、超时、外部接管、模板混合拒绝、失败恢复）。不向真实好友发布。
- 托管 Release 构建通过，24 条既有警告，无错误。
- Python 启动配置相关 9 项、运行库升级/恢复 4 项测试通过；shuffle-core / lyrics-core / media-core 通过。
- 完整 Bridge.Tests 两次在 `FlacTests.ProgressiveCancellation` 第 372 行的释放等待中超时（`FLAC test deadline`），第二次为无并行编译的同一产物重跑。这些测试及相关源码与基线没有差异，且该测试项目未编译本次 Presence 源码；原因未定位，不将整套回归记为通过，不在本调查中扩大到 FLAC 修复。
- 真实游戏 Steam API：A、B 均返回 true，读回一致且无 steam_display；Windows 好友端游戏详情均显示对应文本，B 自动更新由用户确认。
- Windows 好友列表：A/B 对照均只显示游戏名，不显示测试文字。好友端清理：自动变为空白，用户确认并截图核对。悬停卡片实测、精确传播耗时：NotRun；悬停不显示的判断仅有静态依据。
- 完整音乐/计时器功能、长期稳定性和 Intel 实机：未执行。

Windows 11 Parallels 虚拟机的独立好友账号已登录，作为观察端；不发送聊天消息，不改观察账号状态或配置。截图含账号资料时仅本机保存，不纳入 Git。

### A 首轮与菜单缺失复查

游戏运行日志确认：初始 AppID 3548580、键数 0；A 调用返回 true，读回键数 1、status 为固定 A、steam_display 不存在；之后记录有条件恢复完成。Windows 好友列表观察到仍仅显示游戏名。用户随后提供的右键菜单截图没有“查看游戏信息”；由于未将截图时间与发布存续区间同步，不能据此判定 status 始终无法到达好友端，也不能把详情可见记为通过。

Mac Steam 重启后，当前 `sp.js` 哈希已变为 `61048fac50966b6a9ba79ec5a4895a292872d7959b6e93cfa83f47149d3e75e6`（不能沿用上表旧偏移定位新文件）。定向原文检查发现：字节 1831411 附近的菜单构建只有在好友对象存在、非 Gamepad UI，且 `has_server_ip || HasRichPresenceForViewGameInfo()` 为真时加入该入口；字节 6355443 的后者仍检查 status/connect/connect_private。这是本机 Mac 客户端静态条件，不冒充 Windows 客户端已收到 status 的实测证据。

因此第二轮改为在 120 秒清理前重新发布 A、关闭并重新打开 Windows 好友右键菜单，配对游戏日志与好友端截图；不要求用户在无入口的菜单内反复寻找。

### 第二轮 A：好友端详情可见已证实

2026-10-16 19:36（Asia/Shanghai），游戏日志第 80 行再次确认 A 写入成功、读回一致、没有 steam_display。独立 Windows 好友账号的“游戏信息”窗口“状态”栏实际显示完整固定文本 A（窗口内自动折行），旁边好友列表仍仅显示游戏名称。这证明在本游戏 AppID 下无需模板即可向好友的游戏详情传递 status；不证明好友列表主行可自定义，也不证明实际音乐/计时器接入。

本机截图：`.local/steam-status-probe-60618f3/screenshots/windows-status-a-second.png`，SHA-256 `f7ba744b8d0d19f8bb978e90d01e7a3834f6a3f8152cedf2aab04eea6d29af91`，不纳入 Git。首轮菜单缺失不能作为方案不可用证据。

### B：好友端自动更新

2026-10-16 19:38（Asia/Shanghai），游戏日志第 82 行确认仅 status 写入 B、读回一致。Windows 同一“游戏信息 → 状态”栏实际显示 `MusicBridge 计时器测试 B · 专注中，剩余 24 分钟`；用户明确反馈是自动更新，无需手动刷新。画面同时显示好友列表仍只有游戏名。本次没有同步的端到端时钟记录，因此不报告精确秒数延迟。

本机截图：`.local/steam-status-probe-60618f3/screenshots/windows-status-b.png`，SHA-256 `2e3d2c58a8f72bc87245f1ffbbdd88b840a89db8274d3bd90648f06eba2784e8`，不纳入 Git。这里只证明两段固定纯文本可发布和变化，未实现真实曲目与番茄钟数据读取。

### 清理：好友端自动变为空白

2026-10-16 19:40（Asia/Shanghai），用户按要求结束测试并反馈文字自动消失，未手动刷新；游戏日志第 83 行记录恢复完成。随后截图核对 Windows 已打开的游戏信息窗口，“状态”一栏仍在，但内容为空，没有残留 B；游戏名和好友在游戏中的状态正常保留。日志里的“上次发布读回”是历史快照，不表示恢复后仍持有 status。

本机截图：`.local/steam-status-probe-60618f3/screenshots/windows-status-cleared.png`，SHA-256 `4a00429d3fc6b8967588777f66e99dbc5d0fbde4f64bee6ecc07a08b1638a1ee`，不纳入 Git。未单独验证退出游戏时清理，也未测外部游戏写入者竞争；模拟断言不能替代这些实测。

## 本次受测候选

- 实现提交：`60618f313aec69063b48145e45affea58f07d079`。
- 目录：`macos/dist/SteamStatusProbe-60618f3`。
- 托管 DLL SHA-256：`18438732999bc8ab9c4fa448af89efec6f16ab8963977efcd1915def3bc1246d`。
- 核心运行库摘要：`9d5743f020979d255275bc5728bbc8603e6f62a6cfb15f42e3b1ecc94b73cd1b`；原生音频/媒体库哈希与 ExperienceLayoutCandidate 相同，详见候选 release-manifest。
- 已验证启动目标切换、arm64 启动预检及 BepInEx/插件加载；Harmony 22/22。未把启动成功计为 Presence API 或好友端验收。
- 仅复制 BepInEx 配置及主配置；未复制旧 cache/data/logs。原运行目录不变，原启动选项保存在忽略目录 `.local/steam-status-probe-60618f3/switch.json` 及同名 VDF 备份。
- 本轮测试结束后，用户确认已退出 Mac 游戏与 Steam；恢复工具再次检查两者均不运行，已将 AppID 3548580 的 LaunchOptions 恢复至 `ExperienceLayoutCandidate/launch-core.sh`。读回匹配恢复记录，并以恢复前后文本往返比较确认其他 VDF 字节完全不变；备份及探针候选保留。
- 旧版 DLL 复核为 `ca2bdc59bc652cf871ca6297499fa48de2c4067127276c9ad10585593b336468`，探针 DLL 仍与受测哈希一致。此次只确认启动配置恢复，未重新启动旧版游戏，不将旧版重启验收记为通过。Steam 保持退出状态。
- 源码分支保留在本地；没有 Push、PR 或完整功能开发。若以后决定采用“游戏详情状态”作为新目标，需要用户明确确认变更验收范围。

```sh
python3 macos/tools/steam_candidate_options.py restore --record .local/steam-status-probe-60618f3/switch.json
```
