# 无模板 status 实验

2026-10-16，用户在全部语言模板查询为空后，另行授权调查纯 `status`；这不是原好友列表主行功能的自动降级，也不代表音乐/计时器集成已获验收。

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
- 真实游戏 Steam API、Windows 好友列表、悬停卡片、查看游戏信息、A/B 更新和恢复：待测。
- 完整音乐/计时器功能、长期稳定性和 Intel 实机：未执行。

Windows 11 Parallels 虚拟机的独立好友账号已登录，作为观察端；不发送聊天消息，不改观察账号状态或配置。截图含账号资料时仅本机保存，不纳入 Git。
