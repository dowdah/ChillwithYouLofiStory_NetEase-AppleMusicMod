using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicBridge;

// A single non-modal in-game settings surface. The draft never mutates Current.
internal sealed class SettingsPanelUi : MonoBehaviour
{
    private static SettingsPanelUi _instance;
    private readonly string[] _pages = { "播放", "网易云", "缓存", "歌词与迷你条", "系统控制", "诊断" };
    private SettingsDraft _baseline;
    private JObject _working;
    private int _page;
    private bool _saving, _closeAfterSave;
    private readonly HashSet<string> _invalidFields = new HashSet<string>();
    private RectTransform _rect;
    private RectTransform _canvasRect;
    private GameObject _content;
    private GameObject _confirmRow;
    private TextMeshProUGUI _status;
    private Button _save;
    private CacheUsageSnapshot _cacheUsage;
    private CacheCleanupPlan _cachePlan;
    private bool _planningCache, _planningStale;
    private GameObject _confirmCleanupRow, _confirmStaleRow;
    private int _cacheScanGeneration;
    private TextMeshProUGUI _cacheStatus;
    private TextMeshProUGUI _mediaStatus;
    private GameObject _repairConfirmation;

    internal static void Open(Transform from, int page = 0)
    {
        if (_instance != null)
        {
            _instance.gameObject.SetActive(true);
            _instance.ShowPage(page);
            return;
        }
        Canvas canvas = from.GetComponentInParent<Canvas>();
        if (canvas == null) { BridgeLog.Warn("未找到游戏Canvas，设置页无法显示。"); return; }
        var root = new GameObject("MusicBridgeSettings");
        root.transform.SetParent(canvas.transform, false);
        root.transform.SetAsLastSibling();
        _instance = root.AddComponent<SettingsPanelUi>();
        _instance.Build(canvas.transform as RectTransform, page);
    }

    private void Build(RectTransform canvas, int page)
    {
        _canvasRect = canvas;
        _rect = gameObject.AddComponent<RectTransform>();
        _rect.anchorMin = _rect.anchorMax = new Vector2(0.5f, 0.5f);
        _rect.pivot = new Vector2(0.5f, 0.5f);
        Resize();
        Image image = gameObject.AddComponent<Image>();
        image.sprite = UiSprites.Rounded;
        image.type = Image.Type.Sliced;
        image.color = UiKit.DockOpaque;
        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 7f;
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        _baseline = MusicBridgeOptions.Store.Capture(MusicBridgeOptions.Current);
        _working = (JObject)_baseline.Json.DeepClone();
        var header = UiKit.CreateRow(transform, "SettingsTitle", 32f, 8f);
        var title = UiKit.CreateText(header.transform, "音乐设置", 18f, TextAnchor.MiddleLeft);
        title.fontStyle = FontStyles.Bold;
        UiKit.CreateSpacer(header.transform);
        UiKit.CreatePillButton(header.transform, "关闭", false, UiKit.LineColor, 30f, 72f)
            .onClick.AddListener(RequestClose);
        for (int row = 0; row < 2; row++)
        {
            var tabs = UiKit.CreateRow(transform, "SettingsPages" + row, 30f, 5f);
            for (int column = 0; column < 3; column++)
            {
                int index = row * 3 + column;
                UiKit.CreatePillButton(tabs.transform, _pages[index], false, UiKit.LineColor, 28f)
                    .onClick.AddListener(() => ShowPage(index));
            }
        }
        var viewport = new GameObject("SettingsViewport");
        viewport.transform.SetParent(transform, false);
        viewport.AddComponent<RectTransform>();
        var viewportLayout = viewport.AddComponent<LayoutElement>();
        viewportLayout.flexibleHeight = 1f;
        viewportLayout.minHeight = 130f;
        viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
        viewport.AddComponent<RectMask2D>();
        var scroll = viewport.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        _content = UiKit.CreateColumn(viewport.transform, "SettingsContent", 8f, new RectOffset(4, 4, 4, 6));
        var contentRect = _content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        scroll.content = contentRect;

        _status = UiKit.CreateText(transform, "更改设置后点保存；当前播放不会中断。", 12f, TextAnchor.MiddleLeft);
        _status.color = UiKit.TextSecondary;
        _status.enableWordWrapping = true;
        _status.GetComponent<LayoutElement>().preferredHeight = 38f;
        var footer = UiKit.CreateRow(transform, "SettingsFooter", 34f, 6f);
        _save = UiKit.CreatePillButton(footer.transform, "保存", true, UiKit.NeteaseAccent, 32f, 82f);
        _save.onClick.AddListener(() => Save(false));
        UiKit.CreatePillButton(footer.transform, "取消", false, UiKit.LineColor, 32f, 82f)
            .onClick.AddListener(RequestClose);
        UiKit.CreatePillButton(footer.transform, "恢复本页默认", false, UiKit.LineColor, 32f, 138f)
            .onClick.AddListener(RestorePageDefaults);
        _confirmRow = UiKit.CreateRow(transform, "UnsavedConfirmation", 34f, 6f);
        UiKit.CreatePillButton(_confirmRow.transform, "保存并关闭", true, UiKit.NeteaseAccent, 32f, 112f)
            .onClick.AddListener(() => Save(true));
        UiKit.CreatePillButton(_confirmRow.transform, "放弃改动", false, UiKit.LineColor, 32f, 100f)
            .onClick.AddListener(Close);
        UiKit.CreatePillButton(_confirmRow.transform, "继续编辑", false, UiKit.LineColor, 32f, 100f)
            .onClick.AddListener(() => _confirmRow.SetActive(false));
        _confirmRow.SetActive(false);
        ShowPage(page);
    }

    private void Update()
    {
        Resize();
        if (_page == 4 && _mediaStatus != null)
        {
            string status = "实际状态：" + SystemMediaService.StatusText + "；注册命令 " +
                SystemMediaService.RegisteredTargets + "；" + SystemMediaService.Counters;
            if (_mediaStatus.text != status) _mediaStatus.text = status;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) RequestClose();
    }

    private void Resize()
    {
        if (_rect == null || _canvasRect == null) return;
        var desired = new Vector2(Mathf.Min(650f, Mathf.Max(300f, _canvasRect.rect.width - 24f)),
            Mathf.Min(550f, Mathf.Max(270f, _canvasRect.rect.height - 24f)));
        if ((_rect.sizeDelta - desired).sqrMagnitude > 1f) _rect.sizeDelta = desired;
    }

    private void ShowPage(int page)
    {
        _page = Mathf.Clamp(page, 0, _pages.Length - 1);
        _invalidFields.Clear();
        for (int i = _content.transform.childCount - 1; i >= 0; i--)
            Destroy(_content.transform.GetChild(i).gameObject);
        SectionTitle(_pages[_page]);
        switch (_page)
        {
            case 0:
                Toggle("启动时等待手动选择音源", "Shared.PauseGameMusicUntilUserChooses");
                Toggle("队列结束后循环", "Netease.RepeatQueue");
                Toggle("一轮不重复随机", "Netease.NoRepeatShuffle");
                Toggle("下一首预下载", "Netease.NextAudioPreload");
                break;
            case 1:
                Quality();
                Toggle("FLAC边下载边播", "Netease.StreamFlacDuringDownload");
                Toggle("自动刷新喜欢", "Netease.AutoRefreshFavorites");
                Number("喜欢刷新间隔（分钟，1–1440）", "Netease.FavoritesRefreshInterval", 1, 1440,
                    token => (int)TimeSpan.Parse(token.Value<string>(), CultureInfo.InvariantCulture).TotalMinutes,
                    minutes => JToken.FromObject(TimeSpan.FromMinutes(minutes)));
                UiKit.CreatePillButton(_content.transform, "立即刷新喜欢", false, UiKit.LineColor, 30f, 150f)
                    .onClick.AddListener(() => NeteaseRuntime.Favorites.Refresh());
                Note("喜欢上次成功：" + (NeteaseRuntime.Favorites.LastSuccess.HasValue ?
                    NeteaseRuntime.Favorites.LastSuccess.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "尚无记录"));
                break;
            case 2:
                Number("持久音频缓存上限（MiB，0–20480）", "Netease.AudioCacheCapacityBytes", 0, 20480,
                    token => (int)(token.Value<long>() / 1048576L), value => JToken.FromObject(value * 1048576L));
                Number("单文件持久上限（MiB，1–1024）", "Netease.AudioCacheMaximumFileBytes", 1, 1024,
                    token => (int)(token.Value<long>() / 1048576L), value => JToken.FromObject(value * 1048576L));
                Note("0只停止新增持久写入；已有缓存仍可读取。临时播放文件与预下载不受此上限控制。");
                BuildCacheActions();
                break;
            case 3:
                Toggle("显示独立歌词窗", "Overlay.LyricsVisible");
                Toggle("显示迷你播放条", "Overlay.MiniPlayerVisible");
                Toggle("锁定歌词窗位置", "Overlay.LyricsLocked");
                Toggle("锁定迷你条位置", "Overlay.MiniPlayerLocked");
                Toggle("显示歌词译文", "Overlay.ShowTranslation");
                Toggle("无歌词时临时隐藏", "Overlay.HideLyricsWhenUnavailable");
                Percent("歌词字号", "Overlay.LyricsFontScale", 0.75f, 2f);
                Percent("背景不透明度", "Overlay.BackgroundOpacity", 0.15f, 1f);
                UiKit.CreatePillButton(_content.transform, "恢复两个窗口位置", false, UiKit.LineColor, 30f, 160f)
                    .onClick.AddListener(OverlayUi.ResetPositions);
                break;
            case 4:
                Toggle("启用macOS系统媒体控制", "SystemMedia.Enabled");
                _mediaStatus = UiKit.CreateText(_content.transform, "正在读取实际状态…", 12f, TextAnchor.UpperLeft);
                _mediaStatus.enableWordWrapping = true;
                _mediaStatus.GetComponent<LayoutElement>().preferredHeight = 50f;
                UiKit.CreatePillButton(_content.transform, "重试加载桥接", false, UiKit.LineColor, 30f, 150f)
                    .onClick.AddListener(SystemMediaService.Retry);
                Note("当前原型只代理网易云。Apple Music 的系统会话仍由 Music.app 管理；本地音乐待实机验证。 ");
                break;
            default:
                Note("配置来源：" + MusicBridgeOptions.Source);
                Note("配置版本：" + MusicBridgeOptions.Current.SchemaVersion + "；保存能力：" + (MusicBridgeOptions.CanSave ? "可用" : "已锁定"));
                var player = AudioPlayer.Instance;
                Note("请求音质：" + NeteaseQualityPolicy.Label(MusicBridgeOptions.Current.Netease.PreferredQuality) +
                    "；实际音质/格式：" + (player?.PlaybackSource?.QualityLabel ?? "未知") +
                    "；预下载：" + (player?.PrefetchStatus ?? "未启动"));
                Note("媒体桥接：" + SystemMediaService.StatusText + "；" + SystemMediaService.Counters);
                UiKit.CreatePillButton(_content.transform, "打开配置目录", false, UiKit.LineColor, 30f, 150f)
                    .onClick.AddListener(() => OpenDirectory(BridgePaths.Config));
                UiKit.CreatePillButton(_content.transform, "打开日志目录", false, UiKit.LineColor, 30f, 150f)
                    .onClick.AddListener(() => OpenDirectory(BridgePaths.Logs));
                UiKit.CreatePillButton(_content.transform, "修复游戏声音", false, UiKit.LineColor, 30f, 150f)
                    .onClick.AddListener(AudioOutputRecovery.RequestManual);
                BuildDiagnosticsExport();
                BuildExplicitRepair();
                break;
        }
        UpdateSaveButton();
    }

    private void OpenDirectory(string path)
    {
        try { System.Diagnostics.Process.Start("/usr/bin/open", path); }
        catch (Exception ex) { _status.text = "无法打开目录（" + ex.GetType().Name + "）"; }
    }

    private void BuildDiagnosticsExport()
    {
        Note("导出范围：插件/配置版本、媒体可用性、音质与计数；不含账号、曲名、歌单、日志正文或凭据。");
        var row = UiKit.CreateRow(_content.transform, "DiagnosticsExport", 32f, 6f);
        UiKit.CreatePillButton(row.transform, "确认导出脱敏信息", false, UiKit.LineColor, 30f, 174f)
            .onClick.AddListener(() =>
            {
                var report = new JObject
                {
                    ["PluginVersion"] = Plugin.PluginVersion,
                    ["SchemaVersion"] = MusicBridgeOptions.Current.SchemaVersion,
                    ["ConfigWritable"] = MusicBridgeOptions.CanSave,
                    ["MediaStatus"] = SystemMediaService.StatusText,
                    ["RegisteredMediaTargets"] = SystemMediaService.RegisteredTargets,
                    ["MediaCounters"] = SystemMediaService.Counters,
                    ["RequestedQuality"] = NeteaseQualityPolicy.Label(MusicBridgeOptions.Current.Netease.PreferredQuality),
                    ["ActualQuality"] = AudioPlayer.Instance?.PlaybackSource?.QualityLabel ?? "未知",
                    ["PrefetchState"] = AudioPlayer.Instance?.PrefetchStatus ?? "未启动"
                };
                string path = BridgePaths.Resolve("logs", "musicbridge-diagnostics-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json");
                _status.text = "正在导出脱敏信息…";
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string error = null;
                    try { AtomicFile.WriteAllText(path, report.ToString()); }
                    catch (Exception ex) { error = ex.GetType().Name; }
                    MainThreadDispatcher.Enqueue(() => { if (_instance == this)
                        _status.text = error == null ? "已导出到日志目录。" : "导出失败（" + error + "）"; });
                });
            });
    }

    private void BuildExplicitRepair()
    {
        var row = UiKit.CreateRow(_content.transform, "ExplicitConfigRepair", 32f, 6f);
        UiKit.CreatePillButton(row.transform, "显式恢复默认配置", false, UiKit.LineColor, 30f, 160f)
            .onClick.AddListener(() =>
            {
                if (_repairConfirmation != null) return;
                _status.text = "将先备份原配置，再用默认值重建；旧偏好不会自动迁回。请确认。";
                var confirm = UiKit.CreateRow(_content.transform, "ConfirmConfigRepair", 32f, 6f);
                _repairConfirmation = confirm;
                UiKit.CreatePillButton(confirm.transform, "备份并重建", true, UiKit.NeteaseAccent, 30f, 120f)
                    .onClick.AddListener(() =>
                    {
                        Destroy(confirm); _repairConfirmation = null;
                        _status.text = "正在备份并重建配置…";
                        MusicBridgeOptions.Store.RestoreDefaultsAsync(result =>
                        {
                            if (_instance != this) return;
                            if (!result.Success) { _status.text = result.Error; return; }
                            MusicBridgeOptions.Publish(result.Options);
                            _baseline = MusicBridgeOptions.Store.Capture(result.Options);
                            _working = (JObject)_baseline.Json.DeepClone();
                            ShowPage(_page);
                            _status.text = "已备份原配置并恢复默认值。";
                        });
                    });
                UiKit.CreatePillButton(confirm.transform, "继续编辑", false, UiKit.LineColor, 30f, 100f)
                    .onClick.AddListener(() => { Destroy(confirm); _repairConfirmation = null; });
            });
    }

    private void SectionTitle(string value)
    {
        var title = UiKit.CreateText(_content.transform, value, 16f, TextAnchor.MiddleLeft);
        title.fontStyle = FontStyles.Bold;
    }

    private void Note(string value)
    {
        var label = UiKit.CreateText(_content.transform, value, 12f, TextAnchor.UpperLeft);
        label.enableWordWrapping = true;
        label.color = UiKit.TextSecondary;
        label.GetComponent<LayoutElement>().preferredHeight = 42f;
    }

    private void Toggle(string label, string path)
    {
        var row = UiKit.CreateRow(_content.transform, path, 34f, 6f);
        UiKit.CreateText(row.transform, label, 13f, TextAnchor.MiddleLeft);
        UiKit.CreateSpacer(row.transform);
        Button button = UiKit.CreatePillButton(row.transform, "", false, UiKit.LineColor, 30f, 70f);
        var text = button.GetComponentInChildren<TextMeshProUGUI>();
        void Refresh() { text.text = _working.SelectToken(path).Value<bool>() ? "开" : "关"; }
        Refresh();
        button.onClick.AddListener(() => { Set(path, !_working.SelectToken(path).Value<bool>()); Refresh(); });
    }

    private void Quality()
    {
        Note("首选音质：下一次新加载生效，不中断当前曲目。");
        var row = UiKit.CreateRow(_content.transform, "QualityOptions", 32f, 4f);
        var values = new[] { NeteaseQuality.Standard, NeteaseQuality.Exhigh, NeteaseQuality.Lossless, NeteaseQuality.HiRes };
        var buttons = new List<Button>();
        foreach (var value in values)
        {
            var button = UiKit.CreatePillButton(row.transform, NeteaseQualityPolicy.Label(value), false, UiKit.LineColor, 30f);
            buttons.Add(button);
            button.onClick.AddListener(() => { Set("Netease.PreferredQuality", (int)value); RefreshQuality(); });
        }
        void RefreshQuality()
        {
            foreach (var button in buttons)
            {
                var index = buttons.IndexOf(button);
                button.interactable = _working.SelectToken("Netease.PreferredQuality").Value<int>() != (int)values[index];
            }
        }
        RefreshQuality();
    }

    private void Number(string label, string path, int min, int max, Func<JToken, int> display, Func<int, JToken> stored)
    {
        var row = UiKit.CreateRow(_content.transform, path, 34f, 6f);
        UiKit.CreateText(row.transform, label, 12f, TextAnchor.MiddleLeft);
        var input = UiKit.CreateSearchInput(row.transform, "输入数值");
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.text = display(_working.SelectToken(path)).ToString(CultureInfo.InvariantCulture);
        input.onValueChanged.AddListener(value =>
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
                parsed < min || parsed > max)
            {
                _invalidFields.Add(path); _status.text = label + "需在 " + min + "–" + max + " 之间"; UpdateSaveButton(); return;
            }
            _invalidFields.Remove(path);
            Set(path, stored(parsed));
        });
    }

    private void Percent(string label, string path, float min, float max)
    {
        var row = UiKit.CreateRow(_content.transform, path, 34f, 6f);
        var text = UiKit.CreateText(row.transform, label, 12f, TextAnchor.MiddleLeft);
        text.GetComponent<LayoutElement>().preferredWidth = 120f;
        var slider = UiKit.CreateBarSlider(row.transform, true);
        slider.minValue = min; slider.maxValue = max;
        slider.SetValueWithoutNotify(_working.SelectToken(path).Value<float>());
        var value = UiKit.CreateText(row.transform, slider.value.ToString("0.00"), 12f, TextAnchor.MiddleRight);
        value.GetComponent<LayoutElement>().preferredWidth = 45f;
        slider.onValueChanged.AddListener(number => {
            value.text = number.ToString("0.00"); Set(path, number);
            OverlayUi.Preview(_working.SelectToken("Overlay.LyricsFontScale").Value<float>(),
                _working.SelectToken("Overlay.BackgroundOpacity").Value<float>());
        });
    }

    private static string Size(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

    private void BuildCacheActions()
    {
        _cacheStatus = UiKit.CreateText(_content.transform, "正在读取当前账号缓存…", 12f, TextAnchor.UpperLeft);
        _cacheStatus.enableWordWrapping = true;
        _cacheStatus.GetComponent<LayoutElement>().preferredHeight = 94f;
        var row = UiKit.CreateRow(_content.transform, "CacheActions", 34f, 5f);
        UiKit.CreatePillButton(row.transform, "重新扫描", false, UiKit.LineColor, 30f, 100f)
            .onClick.AddListener(RefreshCache);
        UiKit.CreatePillButton(row.transform, "清理当前账号", false, UiKit.LineColor, 30f, 130f)
            .onClick.AddListener(PrepareCleanup);
        UiKit.CreatePillButton(_content.transform, "清理已确认失效的临时文件", false, UiKit.LineColor, 30f, 230f)
            .onClick.AddListener(PrepareStaleCleanup);
        UiKit.CreatePillButton(_content.transform, "停止后续删除", false, UiKit.LineColor, 30f, 130f)
            .onClick.AddListener(() => { AudioDiskCache.CancelActiveCleanup(); _status.text =
                AudioDiskCache.CleanupActive ? "已请求停止；已删除的文件不会恢复。" : "当前没有清理任务。"; });
        UiKit.CreatePillButton(_content.transform, "打开音频缓存目录", false, UiKit.LineColor, 30f, 160f)
            .onClick.AddListener(() =>
            {
                try { System.Diagnostics.Process.Start("/usr/bin/open", AudioDiskCache.Root); }
                catch (Exception ex) { _status.text = "无法打开目录（" + ex.GetType().Name + "）"; }
            });
        var last = AudioDiskCache.LastCleanupResult;
        if (AudioDiskCache.CleanupActive) Note("清理任务仍在后台运行；关闭本页不会取消已确认的清理。");
        else if (last != null) Note("上次清理：删除 " + last.Deleted + " 个文件、" + Size(last.DeletedBytes) +
            "；跳过 " + last.Skipped + " 个，失败 " + last.Failed + " 个。" +
            (last.Temporary ? "（临时文件）" : "（当前账号音频）") + (last.Error ?? ""));
        RefreshCache();
    }

    private void RefreshCache()
    {
        var context = NeteaseRuntime.Context;
        bool signedIn = context != null && context.Active;
        long account = signedIn ? context.UserId : 0;
        int generation = ++_cacheScanGeneration;
        if (_cacheStatus != null) _cacheStatus.text = signedIn ? "正在扫描当前账号音频缓存…" :
            "未登录：正在统计工作文件；当前账号音频缓存不可归属。";
        AudioDiskCache.ScanAsync(account, usage =>
        {
            if (_instance != this || _page != 2 || _cacheStatus == null || generation != _cacheScanGeneration) return;
            if (!ReferenceEquals(context, NeteaseRuntime.Context) || (signedIn && !context.Active))
            { _cacheStatus.text = "账号已变化，请重新扫描。"; return; }
            _cacheUsage = usage;
            long configured = MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes;
            _cacheStatus.text = usage.Error != null ? "扫描失败：" + usage.Error :
                (signedIn ? "当前账号" : "未登录（当前账号不可归属）") + "持久占用 " + Size(usage.PersistentBytes) + "；本次可清理 " + Size(usage.ReclaimableBytes) +
                "；受保护 " + Size(usage.ProtectedBytes) + "；所有账号活动临时 " + Size(usage.ActiveTemporaryBytes) +
                "；未识别/残留 " + Size(usage.ResidualBytes) + "，其中已确认失效临时 " +
                Size(usage.StaleTemporaryBytes) + "。未知文件不会自动删除。\n" +
                "扫描完成 " + usage.CompletedUtc.ToLocalTime().ToString("HH:mm:ss") + "。显示逻辑文件大小。" +
                (usage.PersistentBytes > configured ? " 暂超持久缓存预算，活动文件结束后整理。" : "");
        });
    }

    private void PrepareCleanup()
    {
        if (_planningCache || _confirmCleanupRow != null) return;
        var context = NeteaseRuntime.Context;
        if (context == null || !context.Active) { _status.text = "请先连接网易云账号。"; return; }
        if (AudioDiskCache.CleanupActive) { _status.text = "已有清理任务进行中。"; return; }
        _planningCache = true;
        _status.text = "正在生成当前账号清理计划…";
        AudioDiskCache.PlanAsync(context.UserId, (plan, error) =>
        {
            if (_instance != this) return;
            _planningCache = false;
            if (_page != 2) return;
            if (!ReferenceEquals(context, NeteaseRuntime.Context) || !context.Active)
            { _status.text = "账号已变化，清理计划已取消。"; return; }
            if (error != null) { _status.text = "计划失败：" + error; return; }
            _cachePlan = plan;
            _status.text = "预计清理 " + plan.FileCount + " 个文件、" + Size(plan.EstimatedBytes) +
                "；只清理当前账号未使用的已登记音频，不退出账号。请确认。";
            var row = UiKit.CreateRow(_content.transform, "CacheConfirm", 34f, 6f);
            _confirmCleanupRow = row;
            UiKit.CreatePillButton(row.transform, "确认清理", true, UiKit.NeteaseAccent, 30f, 100f)
                .onClick.AddListener(() => { Destroy(row); _confirmCleanupRow = null; ExecuteCleanup(context, plan); });
            UiKit.CreatePillButton(row.transform, "取消", false, UiKit.LineColor, 30f, 80f)
                .onClick.AddListener(() => { _cachePlan = null; Destroy(row); _confirmCleanupRow = null; _status.text = "已取消清理计划。"; });
        });
    }

    private void ExecuteCleanup(NeteaseAccountContext context, CacheCleanupPlan plan)
    {
        if (_cachePlan != plan || !ReferenceEquals(context, NeteaseRuntime.Context) || !context.Active) return;
        _cachePlan = null;
        _status.text = "正在清理；当前播放和活动预下载会被跳过。";
        AudioDiskCache.ExecuteAsync(plan, () => ReferenceEquals(context, NeteaseRuntime.Context) && context.Active,
            result =>
            {
                if (_instance != this) return;
                _status.text = result.Error ?? ("实际删除 " + result.Deleted + " 个文件、" + Size(result.DeletedBytes) +
                    "；跳过 " + result.Skipped + " 个，失败 " + result.Failed + " 个。正在重新扫描。 ");
                if (_page == 2) RefreshCache();
            });
    }

    private void PrepareStaleCleanup()
    {
        if (_planningStale || _confirmStaleRow != null) return;
        if (AudioDiskCache.CleanupActive) { _status.text = "已有清理任务进行中。"; return; }
        _planningStale = true;
        _status.text = "正在检查临时文件的会话锁…";
        AudioDiskCache.PlanStaleAsync((plan, error) =>
        {
            if (_instance != this) return;
            _planningStale = false;
            if (_page != 2) return;
            if (error != null) { _status.text = "临时文件计划失败：" + error; return; }
            _status.text = "已确认失效的临时文件 " + plan.FileCount + " 个、" + Size(plan.EstimatedBytes) +
                "；仅清理会话锁已释放的本 Mod 新格式文件。请确认。";
            var row = UiKit.CreateRow(_content.transform, "StaleWorkConfirm", 34f, 6f);
            _confirmStaleRow = row;
            UiKit.CreatePillButton(row.transform, "确认清理临时文件", true, UiKit.NeteaseAccent, 30f, 180f)
                .onClick.AddListener(() =>
                {
                    Destroy(row); _confirmStaleRow = null;
                    _status.text = "正在清理已确认失效的临时文件…";
                    AudioDiskCache.ExecuteAsync(plan, null, result =>
                    {
                        if (_instance != this) return;
                        _status.text = result.Error ?? ("实际删除 " + result.Deleted + " 个临时文件、" +
                            Size(result.DeletedBytes) + "；跳过 " + result.Skipped + " 个，失败 " + result.Failed + " 个。");
                        if (_page == 2) RefreshCache();
                    });
                });
            UiKit.CreatePillButton(row.transform, "取消", false, UiKit.LineColor, 30f, 80f)
                .onClick.AddListener(() => { Destroy(row); _confirmStaleRow = null; _status.text = "已取消临时文件清理计划。"; });
        });
    }

    private void Set(string path, object value)
    {
        var parts = path.Split('.');
        JObject node = _working;
        for (int i = 0; i < parts.Length - 1; i++) node = (JObject)node[parts[i]];
        node[parts[parts.Length - 1]] = value is JToken token ? token.DeepClone() : JToken.FromObject(value);
        _status.text = "有未保存的修改；保存后按设置项说明生效。";
        UpdateSaveButton();
    }

    private void RestorePageDefaults()
    {
        var defaults = JObject.FromObject(new MusicBridgeOptions());
        string[][] paths = {
            new[] { "Shared.PauseGameMusicUntilUserChooses", "Netease.RepeatQueue", "Netease.NoRepeatShuffle", "Netease.NextAudioPreload" },
            new[] { "Netease.PreferredQuality", "Netease.StreamFlacDuringDownload", "Netease.AutoRefreshFavorites", "Netease.FavoritesRefreshInterval" },
            new[] { "Netease.AudioCacheCapacityBytes", "Netease.AudioCacheMaximumFileBytes" },
            new[] { "Overlay.LyricsVisible", "Overlay.MiniPlayerVisible", "Overlay.LyricsLocked", "Overlay.MiniPlayerLocked", "Overlay.ShowTranslation", "Overlay.HideLyricsWhenUnavailable", "Overlay.LyricsFontScale", "Overlay.BackgroundOpacity" },
            new[] { "SystemMedia.Enabled" }, Array.Empty<string>()
        };
        foreach (string path in paths[_page]) Set(path, defaults.SelectToken(path));
        ShowPage(_page);
        if (_page == 3) OverlayUi.Preview(_working.SelectToken("Overlay.LyricsFontScale").Value<float>(),
            _working.SelectToken("Overlay.BackgroundOpacity").Value<float>());
        _status.text = "已恢复本页默认值到草稿；登录、缓存和游戏存档未更改。";
    }

    private void UpdateSaveButton()
    {
        if (_save != null) _save.interactable = !_saving && _invalidFields.Count == 0 && MusicBridgeOptions.CanSave &&
            !JToken.DeepEquals(_working, _baseline.Json);
    }

    private void Save(bool closeAfter)
    {
        if (_saving || _invalidFields.Count != 0 || !MusicBridgeOptions.CanSave) return;
        JObject patch = Diff(_baseline.Json, _working);
        if (!patch.HasValues) { if (closeAfter) Close(); return; }
        _saving = true; _closeAfterSave = closeAfter;
        _status.text = "正在保存设置…";
        UpdateSaveButton();
        MusicBridgeOptions.Store.SavePatchAsync(_baseline, patch, result =>
        {
            if (_instance != this) return;
            _saving = false;
            if (!result.Success)
            { _status.text = result.Error; UpdateSaveButton(); return; }
            MusicBridgeOptions.Publish(result.Options);
            OverlayUi.ClearPreview();
            _baseline = MusicBridgeOptions.Store.Capture(result.Options);
            _working = (JObject)_baseline.Json.DeepClone();
            _status.text = "已保存；播放参数下次切歌生效，显示参数立即生效。";
            ShowPage(_page);
            if (_closeAfterSave) Close();
        });
    }

    private static JObject Diff(JObject original, JObject working)
    {
        var result = new JObject();
        foreach (var property in working.Properties())
        {
            if (property.Value is JObject nested && original[property.Name] is JObject oldNested)
            {
                JObject child = Diff(oldNested, nested);
                if (child.HasValues) result[property.Name] = child;
            }
            else if (!JToken.DeepEquals(original[property.Name], property.Value))
                result[property.Name] = property.Value.DeepClone();
        }
        return result;
    }

    private void RequestClose()
    {
        if (_saving) return;
        if (!JToken.DeepEquals(_working, _baseline.Json))
        {
            _confirmRow.SetActive(true);
            _status.text = "有未保存的修改。保存并关闭、放弃改动，或继续编辑。";
            return;
        }
        Close();
    }

    private void Close() { OverlayUi.ClearPreview(); Destroy(gameObject); }
    private void OnDestroy() { OverlayUi.ClearPreview(); if (_instance == this) _instance = null; }
}
