# MusicBridge 体验增强验收记录

本记录只覆盖 `codex/experience-enhancements` 的候选实现。历史 [第一批](NETEASE-PHASE1.md) 与 [第二批](NETEASE-PHASE2.md) 验收保持原样，不视为本批证据。

## 实施前真实基线（2026-09-26）

| 项目 | 读取结果 |
|---|---|
| `git rev-parse HEAD` | `3084e9f411d0290dcee43b1961848fcb302c5fb0` |
| `git status --short` | 空；无未提交改动 |
| 原分支 | `main`，随后新建 `codex/experience-enhancements` |
| 插件声明版本 | `1.3.0.0`；本候选源码升为 `1.5.0.0` |
| 游戏 | Steam 安装的 1.17.3，Info.plist `CFBundleVersion=0` |
| Unity | `globalgamemanagers` 中 2022.3.62f2 |
| macOS/CPU | macOS 27.0 (26A428)，arm64；游戏可执行文件含 arm64/x86_64 切片 |
| .NET SDK | 仓库 `macos/global.json` 选 8.0.425；从仓库根直接调用 `dotnet` 为 10.0.301 |
| 原生 SDK | `xcrun --show-sdk-version` 为 27.0 |
| 运行库来源 | 本地候选基于仓库 `macos/build.sh` 固定的 BepInEx/Harmony 源和本机 Steam 游戏 Mono/Unity 程序集；不是 .NET 8 运行时插件 |
| 旧候选托管 DLL SHA-256 | `macos/dist/ChillMusicMac/.../MusicBridge.Plugin.dll`：`0bd840bbff38875c2519693595b6a7bdf9a01547f30806aaa18accb45f10afd3` |
| 旧候选 BepInEx/Harmony SHA-256 | `BepInEx.dll`：`581ea5f250947d5c7d59c30c2c8364b2641bd35d876e98714aa72387a8974938`；`0Harmony.dll`：`f460f30fae1b27dbcf3c6127c7e41f1da57f6c920cb06433689ad0e85245b446` |

上述旧候选哈希只用于固定本机来源，不代表本批运行证据。

## 隔离候选产物

`macos/dist/ExperienceCandidate` 由本分支源码构建，不覆盖现用 `ChillMusicMac`。构建时复用全文件摘要 `c04d6c416cebe306a1df15f787b13c81c2d848b9774d1ef760c872ff382b7a54` 的本机原生运行库缓存；完整重编译曾在 NuGet 恢复阶段因网络不可达停止。候选媒体 dylib 已用 `file` 与 `lipo -verify_arch` 验证 arm64/x86_64，包内没有 `config/cache/logs`。

| 候选二进制 | SHA-256 |
|---|---|
| `MusicBridge.Plugin.dll` | `c3a817dde60c41f8ca2112229a654ee6d466913afb85a628f5785909e8420b4f` |
| `libmusicbridge_flac.dylib` | `4933309a9cf37a8d9c4c3bec373f065040dbf77224c9bfb67844e31fa505e091` |
| `libmusicbridge_media.dylib` | `34a05144132d867278279cfb9ddc08a002185901949163d31ccb9536201712dd` |

候选目录的 `release-manifest.json` 绑定源码提交、二进制哈希、配置 schema 和未执行门槛；游戏验收或后续源码修改后必须重新构建并更新清单。

## 实现和验证状态

| 工作包 | 当前代码 | 离线验证 | 游戏/系统验收 |
|---|---|---|---|
| A 设置页与 V2 配置 | 六分区设置入口、草稿、保存/取消、本页默认、逐项生效状态、字段冲突、V1 备份、显式修复 | 迁移、备份、冲突、旧回调修订号门控、未知字段/未来版本通过 | NotRun：布局、焦点、预览取消和实际保存反馈 |
| B 音频缓存 | 当前账号统计、可取消流式扫描、受租约保护的计划与确认清理、实际字节结果、异步容量整理；新格式旧会话临时文件另行确认清理 | 已登记文件、租约、目录相对删除、符号链接、所有权命名、会话锁与大目录扫描通过 | NotRun：边播边清理、跨进程并发和真实路径替换；旧格式/无会话标记残留仍只统计 |
| C 游戏内视图 | 独立歌词窗、迷你条、活动音源快照、无消费歌词读取、归一化布局 | 纯歌词快照多视图/seek/切曲用例与托管构建通过 | NotRun：可见、拖动/锁定、场景重建、Apple/本地真实交接、焦点截图 |
| D 系统媒体控制 | 双架构公开 API 原生桥接、托管 FIFO 轮询、ABI 2 原生计数、默认关闭与失败降级 | 原生构建、ABI、入队/拒绝/队列计数和命令门控通过 | NotRun：真实游戏宿主核心原型；本地音乐代理尚未启用。见 [原型记录](MEDIA-CONTROL-POC.md) |
| E 随机与预下载 | 网易云普通队列按 SongId 洗牌、稳定候选、确认历史、预下载计划绑定 | 10000 首模拟队列、授权匹配与租约测试通过 | NotRun：真实音频出声、快速切歌、长稳与性能对照 |

## 已执行命令与边界

- `git diff --check`：通过。
- `(cd macos && dotnet run --project tests/Bridge.Tests.csproj --no-restore)`：1099 项 `PASS`；因沙箱内系统主机名查询使 `CookieContainer` 初始化失败，在获准的本机执行环境补跑。一次旧 FLAC 取消用例在 10 秒等待处超时，同一套件复跑通过；这项抖动尚不能作为长稳证据。
- `DOTNET_CLI_HOME=/private/tmp/musicbridge-dotnet dotnet run --project tests/shuffle-core/ShuffleCore.Tests.csproj --no-restore`：通过。
- `bash macos/build-media-native.sh`：双架构 dylib 构建通过；`nm -gU` 可见 ABI 符号。
- 可取消缓存扫描的合成 100/1000/10000 文件样本分别约 8/31/337 ms；扫描期间前台临时文件准备不等待整轮 I/O 锁。10000 首随机计划离线推进约 2 ms。这些是本机模拟样本，不替代游戏帧时间或真实缓存规模测量。
- `python3 -m unittest discover -s macos/tools -p 'test_*.py'`：8 项通过，包含升级工具的配套二进制与 V1 配置回滚模拟。
- 插件 `netstandard2.1` 托管构建通过，现有警告保留。编译不证明游戏 Mono 兼容、UI 可见、音频出声或媒体系统 UI 生效。
- 确定性构建开关设置后，两次独立 `-t:Rebuild` 的托管 DLL 哈希一致；本次代码变更后候选 DLL 为上表 `c3a817dd…`。文件版本与插件声明为 1.5.0.0 候选。
- `frontend-design-premium` 严格静态审计为 0 项发现，JSON 保存在候选目录 `experience-ui-audit.json`。`DESIGN.md` 的 `npx` lint 因本机缺少已缓存工具且网络不可达未完成；Unity 游戏视图截图仍 NotRun。
- 完整 `build.sh` 的固定 BepInEx 源码重编译因 NuGet 恢复无网络而停止；隔离候选包通过 `MUSICBRIDGE_NATIVE_CORE_DIGEST=c04d6c416cebe306a1df15f787b13c81c2d848b9774d1ef760c872ff382b7a54` 验证已有本机 `macos/.downloads/native-core` 的全文件摘要后构建。该复用来源与新源码构建不能混称。

## 发布门槛

最终候选必须经正常 Steam 游戏入口完成设置、缓存、双窗和系统控制核心实机矩阵，再做至少 60 分钟最终产物长稳与真实回滚。缺库降级、Music.app 交接、当前播放清理安全或配置回滚任一失败，都不能宣布四项完整交付。未持有的 Intel 环境应单列 NotRun。
