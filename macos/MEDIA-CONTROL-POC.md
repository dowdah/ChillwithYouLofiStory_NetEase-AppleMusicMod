# macOS 系统媒体控制原型记录

## 原型边界

本候选使用进程内 `MediaPlayer.framework` 的 `MPNowPlayingInfoCenter` 和 `MPRemoteCommandCenter`，由自有双架构 `libmusicbridge_media.dylib` 暴露 64 字节、ABI 1 的 C 结构。原生回调只写入 64 项有界 FIFO；Unity 常驻调度器最多每帧处理 16 条，超过 2 秒或音源 epoch 过期的命令被丢弃。连续 Next 保留各自序号。失去动态库、ABI 不符或公开 API 不可用时，普通播放继续工作。

系统控制默认关闭。当前原型只发布已由用户实际选择的网易云歌曲；切到 Apple Music 时移除本 Mod 的注册目标和元数据，让 Music.app 接管。本地原生音乐代理尚未验证，因此原型不发布其系统会话。游戏内迷你条仍可走原有 Apple Music 控制链路。

## 环境和已执行证据

- 宿主待测：Steam 版游戏 1.17.3，Unity 2022.3.62f2，macOS 27.0 (26A428)，arm64。本记录尚无该游戏进程内的系统 UI 截图。
- 原生 SDK：macOS 27.0；脚本 `bash macos/build-media-native.sh` 已在本机成功构建、签名并验证 arm64 与 x86_64 切片。x86_64 只有编译验证，没有 Intel 游戏实测。
- `nm -gU` 已检查七个 `mb_media_*` 导出符号。`python3 -m unittest discover -s macos/tools -p 'test_*.py'` 包含 ABI、无会话初始化和无效快照拒绝测试。
- 当前候选媒体 dylib 的 SHA-256 为 `7dbf36308926a139ed3b68f2add85b99b34a845f68aec3a80fc543973ca528cb`；当前托管 DLL 为 `853347d417d619357066bf68e648bc41fbde837c806e7d3914c668672bd037ed`。这些哈希只证明打包一致，不证明游戏宿主里的系统 UI 生效。
- 托管离线测试覆盖两条连续 Next、旧 seek、过期命令、旧 epoch、重复序号和重试序号重置。

## 游戏宿主验收（全部 NotRun）

| 场景 | 需要看到的结果 | 状态 |
|---|---|---|
| 加载与降级 | 候选 dylib 在 Steam 启动的游戏进程加载；缺库时游戏仍可播放 | NotRun |
| 网易云元数据 | 系统“正在播放”显示真实标题、进度和播放/暂停状态 | NotRun |
| 前后台命令 | 游戏前台、切换其他 App、最小化时 Play/Pause/Next 到达同一传输层 | NotRun |
| Music.app 交接 | 网易云→Apple Music→网易云，不重复执行系统命令、不抢回会话 | NotRun |
| 退出与重建 | 设置页反复开关不重复注册，正常退出后本 Mod 目标归零 | NotRun |

任何一项核心场景失败时，系统控制维持实验性/未通过状态。原生 API 返回受理只证明命令入队；音频出声和系统卡片必须另行观察。

## 参考

Apple 对 [macOS 的 playbackState](https://developer.apple.com/documentation/mediaplayer/mpnowplayinginfocenter/playbackstate) 要求在播放开始和停止时更新。命令处理仅通过 [公开的 MPRemoteCommand 注册与移除接口](https://developer.apple.com/documentation/mediaplayer/mpremotecommand) 管理本 Mod 的令牌；不使用私有 MediaRemote、全局按键监听或静音音轨。
