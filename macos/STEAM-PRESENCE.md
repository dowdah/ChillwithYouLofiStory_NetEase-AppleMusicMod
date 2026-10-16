# Steam 好友状态：阶段一调查与探针

日期：2026-10-16。分支：`feature/steam-rich-presence`，基线 `843ae988855a482f13d509dd708235a6ec38b9d2`。

## 当前结论与阶段门槛

**阶段一探针已实现；AppID 3548580 的可复用显示模板尚未确认，好友列表显示验收未执行。**

没有把“无法查到”写成“确定不存在”。按用户确认的计划，此状态不允许继续音乐/计时器完整开发，不改为详情显示，不 Push、不创建 PR。现有 Steam 启动目标、已验收运行目录及配置未改动；因此无需恢复启动选项。源码插件版本仍为 1.5.0.0，只有完整功能进入交付才升级到 1.6.0.0。本探针不作为新版正式发行。

## 静态调查证据

| 对象 | SHA-256 | 观察 |
| --- | --- | --- |
| 游戏 `Assembly-CSharp.dll` | `32e6c68ec92a8e8e905a2fe23376c7a69b65563d2ba473de5f1c1fba08054f2d` | 2,587 个类型、14,101 个有方法体的方法中，没有发现方法名含 RichPresence 的直接调用，也没有发现 steam_display、#Status 前缀或含 RichPresence 的字符串常量 |
| 游戏 `com.rlabrecque.steamworks.net.dll` | `091c5ce8ec21e3e9de8c89293f4035671f82743803d9e4857f9d82a54924e04c` | 存在 SetRichPresence、读取自身键的接口、GetHSteamUser、BLoggedOn、GetSteamID、GetAppID 及所需结构字段 |

`NestopiSystem.Steam.SteamManager` 已负责 SteamAPI.Init、RunCallbacks、Shutdown。探针不接管这些生命周期。

此前 REA 静态工具在该主程序集上触及元数据表示预算，不能提供完整方法级证据。本次使用仓库新增的 Cecil 离线检查器获得上述统计；检查器解析元数据/CIL，不加载执行游戏代码。它不证明游戏运行行为，不枚举 Steam 后台模板，也不能排除反射、其他程序集或动态构造字符串。

检查器使用现有 `macos/.downloads/native-core/Mono.Cecil.dll`，无需下载新的反编译器。复查命令（在 `macos` 目录内执行）：

```sh
dotnet run --project tools/presence-inspect -- '/absolute/path/to/Managed/Assembly-CSharp.dll'
dotnet run --project tools/presence-inspect -- '/absolute/path/to/Managed/com.rlabrecque.steamworks.net.dll'
```

## 模板来源检查

- [Steamworks 官方 Rich Presence 文档](https://partner.steamgames.com/doc/api/ISteamFriends#richpresencelocalization)：好友列表依赖该 AppID 已发布的有效本地化模板与对应参数；设置 API 返回 true 不代表已渲染。
- [Steam 官方测试页](https://steamcommunity.com/dev/testrichpresence)：匿名读取重定向到登录页，没有取得当前用户的模板诊断。未读取用户浏览器 Cookie 或登录凭据。
- [SteamDB 本游戏模板页面](https://steamdb.info/app/3548580/localization/)：HTTP 403/访问挑战，不能据此推断模板存在或不存在。
- 尝试的匿名 `ICommunityService/GetAppRichPresenceLocalization/v1/` HTTP 路径返回 404：这不是一份“模板为空”的有效接口响应，不能作为不存在的证据。
- 针对本 AppID/游戏名的公开检索未取得可引用的 token/参数映射。HOI4 与 GMod 的模板不属于本 AppID，未写入候选清单。

`SteamPresenceProbeCatalog.Profiles` 因此保持空。自动测试里的 `#UnitTestOnly` 仅供模拟，不编译进插件候选清单。

## 已实现的探针行为

源码构建后，入口为“音乐设置 → 诊断 → Steam 好友状态能力探针”。本轮未安装到游戏，现用版本不会出现该入口。

- “只读检查 Steam”：仅在用户点击时绑定游戏已加载的 Steamworks.NET。先检查初始化用户句柄，再检查登录与 AppID；显示键数量、安全键名和合法格式的 steam_display token，隐藏其他值及身份数据。
- “发布固定测试 A/B”：只有录入一个带来源的经过代码审查的 token/文本参数映射时才可用。本次两按钮禁用；不存在自动猜测或自由编辑 token 入口。
- 将来可用时发布固定的 `MusicBridge 测试 A` / `MusicBridge 测试 B`，先写参数、后写显示 token。UI 分开表示 API 成功和好友端尚待确认。
- “结束测试并恢复”：只恢复本模块仍持有的键；外部改写保留。测试在最后一次成功发布 120 秒后自动恢复，不要求设置窗口保持打开。
- 临时断线或恢复失败保留待恢复项，每 5 秒重试。Steam 账号/会话改变时丢弃旧会话恢复数据，绝不写到新会话；退出时最后尝试一次，接口不可用时不声称恢复成功。
- 原值仅存内存，不写配置或日志。没有 Init、Shutdown、RunCallbacks 或 ClearRichPresence 调用。Steam 不提供原子 compare-and-set，逐键检查与写入之间的外部竞争仍需实机验证。

目前的探针 profile 只支持单个直接文本占位参数。若后续证据显示模板需要多个固定参数或嵌套 token，必须先按实证扩展映射和测试；不能直接猜测补值。

## 本次验证

| 项目 | 结果 |
| --- | --- |
| 插件 netstandard2.1 Release 构建 | Pass，既有警告保留 |
| presence-core 模拟 Steam 测试 | 32 项断言 Pass；生产空清单禁止写入、签名绑定、就绪/离线/AppID、读回、A/B 切换、部分/不确定写入恢复、外部改写、跨账号隔离、超时与退出清理 |
| 现有 Bridge.Tests 完整默认回归 | Pass；沙箱首次因 CookieContainer 的 GetDomainName 限制失败，宿主环境使用同一测试产物补跑通过 |
| shuffle-core / lyrics-core / media-core | Pass |
| Python 工具测试 | 11 项通过；只在临时目录模拟既有安装/回滚逻辑 |
| git diff --check | Pass |
| 探针游戏 Mono/UI/Steam 真实调用 | NotRun |
| 好友列表 A/B 与恢复验收 | NotRun：模板未确认 |
| 音乐/计时器集成、独立配置、完整候选部署 | 尚未开始，受阶段一门槛约束 |

本次仅构建的 `macos/src/bin/Release/netstandard2.1/MusicBridge.Plugin.dll` SHA-256 为 `e5f5a3f90c1ea973bd9f8e5d7d263ce9191dbbb94c7195ef9793db4c878e4ad4`；未复制到现用运行目录，不能把此哈希当作游戏已加载产物。

离线测试命令：

```sh
cd macos
dotnet run --project tests/presence-core
dotnet run --project tests/Bridge.Tests.csproj
dotnet run --project tests/shuffle-core
dotnet run --project tests/lyrics-core
dotnet run --project tests/media-core
```

这些测试不向真实 Steam 好友发布内容。完整默认回归包含模拟传输及本地回环测试，不启用 `--music --network --keychain` 的真实系统集成选项。

## 恢复工作的必要证据

需要取得 **AppID 3548580 自己的 token、本地化模板文本及占位参数**，或可进一步解析这些信息的已登录官方测试页结果。只看到“游戏包含 Steamworks.NET”不足以解除门槛。

有证据后，在同一功能分支录入匹配模板，构建独立探针候选，完成好友端 A/B 更新与恢复；用户确认该阶段通过之后，才进入音乐/计时器接入。不要因 SDK 返回 true 而自动跨过验收门槛。
