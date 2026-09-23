# 启动前检查探针（开发用）

此工具仅用于隔离检查，不能放进正常运行的游戏插件目录。它无论成功或失败都会退出进程。

先构建 Mac 插件和运行库；再在独立游戏副本旁准备单独的 BepInEx 目录，把原生 core、插件 DLL 和 music.js 复制进去。编译此工程后，把 `MusicBridge.Probe.dll` 放入该副本的 `BepInEx/core`，将 Doorstop 的目标设置为探针 DLL。

运行时需要设置 `CHILL_PROBE_MANAGED` 为副本的 Managed 目录，`CHILL_PROBE_LOG` 为独立日志文件，并按正常加载方式设置 Doorstop 环境。使用 `-batchmode -nographics`，外层进程应设定超时；不要把探针的启动设置写入 Steam。

探针会检查 Harmony、Music 自动化、临时钥匙串、网易云匿名二维码接口及游戏挂钩方法，可能触发系统 Music 自动化授权。需要网络和已可访问的音乐 App；不播放歌曲，不进行网易云登录。仅在独立游戏副本中测试，避免中断正在游玩的进程。

## 第二批纯FLAC探针

在上述隔离副本中额外设置 `CHILL_PROBE_FLAC` 为
`tools/make_flac_fixtures.py` 生成的 `192000-24-2.flac` 的绝对路径。
此模式绕过 Music 自动化、钥匙串写入、二维码和游戏场景，
只在游戏Mono下反射调用当前插件的FLAC完整校验、原生读取/seek/关闭。
正常结果包含 `Game Mono native ABI/read/seek/close passed; active handles=0`。
它不验证Unity AudioClip、真实出声或设备切换，不能替代游戏实测。

设置 `CHILL_PROBE_HTTP_FLAC` 为同一合成文件可运行纯本地HTTP故障探针：
第一个响应截断，第二个完整；要求游戏Mono下恰好重试一次、完整校验和临时文件清理成功。
此模式不访问网易云账号、不进入游戏场景。它与 `CHILL_PROBE_FLAC` 分开运行。
