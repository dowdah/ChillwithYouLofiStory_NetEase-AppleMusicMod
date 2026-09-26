using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicBridge;

internal static class OverlayUi
{
    private sealed class Window
    {
        internal GameObject Root;
        internal RectTransform Rect;
        internal OverlayDragHandle Drag;
        internal Image Background, Cover;
        internal TextMeshProUGUI Header, Title, Artist, Original, Translation, Time, PlayLabel;
        internal Button Previous, Next, Play;
        internal TextMeshProUGUI LockLabel;
        internal Slider Progress, Volume;
        internal NeteaseFavoriteButton Favorite;
        internal string TrackKey;
        internal long ScrubEpoch;
        internal string ScrubTrackKey;
        internal bool Scrubbing;
        internal bool VolumeDragging;
    }

    private static Canvas _canvas;
    private static RectTransform _canvasRect;
    private static Window _lyrics, _mini;
    private static float? _fontPreview, _opacityPreview;
    private static float _lastCanvasWidth, _lastCanvasHeight;
    private static float _nextCanvasSearch;
    internal static int InstanceCount => (_lyrics != null ? 1 : 0) + (_mini != null ? 1 : 0);
    private static string _operationError;
    private static float _operationErrorUntil;

    internal static void Attach(Canvas canvas)
    {
        if (canvas == null || canvas == _canvas) return;
        DestroyWindow(ref _lyrics); DestroyWindow(ref _mini);
        _canvas = canvas; _canvasRect = canvas.transform as RectTransform;
        _lastCanvasWidth = _lastCanvasHeight = 0;
    }

    internal static void Tick()
    {
        var options = MusicBridgeOptions.Current.Overlay;
        if (_canvas != null && !_canvas.gameObject.activeInHierarchy)
        { DestroyWindow(ref _lyrics); DestroyWindow(ref _mini); _canvas = null; _canvasRect = null; }
        if (_canvas == null && (options.LyricsVisible || options.MiniPlayerVisible) &&
            Time.unscaledTime >= _nextCanvasSearch)
        {
            _nextCanvasSearch = Time.unscaledTime + 2f;
            Canvas choice = null; float area = 0;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                var rect = canvas.transform as RectTransform;
                if (rect == null || canvas.renderMode == RenderMode.WorldSpace) continue;
                float candidate = rect.rect.width * rect.rect.height;
                if (candidate > area) { choice = canvas; area = candidate; }
            }
            Attach(choice);
        }
        if (_canvas == null || _canvasRect == null) return;
        if (options.LyricsVisible && _lyrics == null) _lyrics = BuildLyrics();
        if (!options.LyricsVisible && _lyrics != null) DestroyWindow(ref _lyrics);
        if (options.MiniPlayerVisible && _mini == null) _mini = BuildMini();
        if (!options.MiniPlayerVisible && _mini != null) DestroyWindow(ref _mini);
        if (_lyrics == null && _mini == null) return;
        if (Mathf.Abs(_canvasRect.rect.width - _lastCanvasWidth) > 1f ||
            Mathf.Abs(_canvasRect.rect.height - _lastCanvasHeight) > 1f)
        {
            _lastCanvasWidth = _canvasRect.rect.width; _lastCanvasHeight = _canvasRect.rect.height;
            ResizeWindow(_lyrics, 420f, 118f);
            ResizeWindow(_mini, 430f, 204f);
            _lyrics?.Drag?.Clamp(); _mini?.Drag?.Clamp();
        }
        PlaybackSnapshot snapshot = PlaybackSnapshotService.Capture();
        if (_lyrics != null) UpdateLyrics(_lyrics, snapshot, options);
        if (_mini != null) UpdateMini(_mini, snapshot, options);
    }

    internal static void Preview(float fontScale, float opacity)
    { _fontPreview = fontScale; _opacityPreview = opacity; }

    internal static void ClearPreview()
    { _fontPreview = null; _opacityPreview = null; }

    internal static void ResetPositions()
    {
        OverlayLayoutStore.Reset();
        if (_lyrics != null) Place(_lyrics, true);
        if (_mini != null) Place(_mini, false);
    }

    private static void DestroyWindow(ref Window window)
    {
        if (window?.Root != null) UnityEngine.Object.Destroy(window.Root);
        window = null;
    }

    private static void ResizeWindow(Window window, float width, float height)
    {
        if (window?.Rect == null) return;
        Vector2 desired = new Vector2(Mathf.Min(width, Mathf.Max(220f, _canvasRect.rect.width - 20f)),
            Mathf.Min(height, Mathf.Max(90f, _canvasRect.rect.height - 20f)));
        if ((window.Rect.sizeDelta - desired).sqrMagnitude > 1f) window.Rect.sizeDelta = desired;
    }

    private static Window BaseWindow(string name, Vector2 size, bool lyrics)
    {
        var window = new Window();
        window.Root = new GameObject(name);
        window.Root.transform.SetParent(_canvas.transform, false);
        window.Root.transform.SetAsLastSibling();
        window.Rect = window.Root.AddComponent<RectTransform>();
        window.Rect.anchorMin = window.Rect.anchorMax = new Vector2(0.5f, 0.5f);
        window.Rect.pivot = new Vector2(0.5f, 0.5f);
        window.Rect.sizeDelta = new Vector2(Mathf.Min(size.x, _canvasRect.rect.width - 20f), size.y);
        window.Background = window.Root.AddComponent<Image>();
        window.Background.sprite = UiSprites.Rounded;
        window.Background.type = Image.Type.Sliced;
        window.Background.color = UiKit.DockOpaque;
        var column = window.Root.AddComponent<VerticalLayoutGroup>();
        column.spacing = 5f;
        column.padding = new RectOffset(10, 10, 8, 8);
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        column.childControlWidth = true;
        column.childControlHeight = true;
        Place(window, lyrics);
        return window;
    }

    private static void Place(Window window, bool lyrics)
    {
        var layout = OverlayLayoutStore.Current;
        float x = lyrics ? layout.LyricsX : layout.MiniX;
        float y = lyrics ? layout.LyricsY : layout.MiniY;
        window.Rect.anchoredPosition = new Vector2((x - 0.5f) * _canvasRect.rect.width,
            (y - 0.5f) * _canvasRect.rect.height);
        window.Drag?.Clamp();
    }

    private static GameObject Header(Window window, bool lyrics)
    {
        var row = UiKit.CreateRow(window.Root.transform, "DragTitle", 30f, 5f);
        var image = row.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.02f);
        window.Header = UiKit.CreateText(row.transform, lyrics ? "歌词" : "迷你播放", 13f, TextAnchor.MiddleLeft);
        window.Header.fontStyle = FontStyles.Bold;
        UiKit.CreateSpacer(row.transform);
        window.Drag = row.AddComponent<OverlayDragHandle>();
        window.Drag.Window = window.Rect;
        window.Drag.CanvasRect = _canvasRect;
        window.Drag.Canvas = _canvas;
        window.Drag.Lyrics = lyrics;
        return row;
    }

    private static Window BuildLyrics()
    {
        var window = BaseWindow("MusicBridgeLyricsOverlay", new Vector2(420f, 118f), true);
        var header = Header(window, true);
        UiKit.CreatePillButton(header.transform, "译文", false, UiKit.LineColor, 26f, 58f)
            .onClick.AddListener(() => SaveOverlayOption("ShowTranslation", !MusicBridgeOptions.Current.Overlay.ShowTranslation));
        var lockButton = UiKit.CreatePillButton(header.transform, "锁定", false, UiKit.LineColor, 26f, 54f);
        window.LockLabel = lockButton.GetComponentInChildren<TextMeshProUGUI>();
        lockButton.onClick.AddListener(() => SaveOverlayOption("LyricsLocked", !MusicBridgeOptions.Current.Overlay.LyricsLocked));
        UiKit.CreatePillButton(header.transform, "设置", false, UiKit.LineColor, 26f, 54f)
            .onClick.AddListener(() => SettingsPanelUi.Open(window.Root.transform, 3));
        UiKit.CreatePillButton(header.transform, "关闭", false, UiKit.LineColor, 26f, 54f)
            .onClick.AddListener(() => SaveOverlayOption("LyricsVisible", false));
        window.Original = UiKit.CreateText(window.Root.transform, "歌词：未播放", 16f, TextAnchor.MiddleCenter);
        window.Original.enableWordWrapping = true;
        window.Original.GetComponent<LayoutElement>().preferredHeight = 34f;
        window.Translation = UiKit.CreateText(window.Root.transform, "", 13f, TextAnchor.MiddleCenter);
        window.Translation.enableWordWrapping = true;
        window.Translation.color = UiKit.TextSecondary;
        window.Translation.GetComponent<LayoutElement>().preferredHeight = 28f;
        Place(window, true);
        return window;
    }

    private static Window BuildMini()
    {
        var window = BaseWindow("MusicBridgeMiniPlayer", new Vector2(430f, 204f), false);
        var header = Header(window, false);
        var lockButton = UiKit.CreatePillButton(header.transform, "锁定", false, UiKit.LineColor, 26f, 54f);
        window.LockLabel = lockButton.GetComponentInChildren<TextMeshProUGUI>();
        lockButton.onClick.AddListener(() => SaveOverlayOption("MiniPlayerLocked", !MusicBridgeOptions.Current.Overlay.MiniPlayerLocked));
        UiKit.CreatePillButton(header.transform, "曲库", false, UiKit.LineColor, 26f, 52f)
            .onClick.AddListener(() =>
            {
                if (!BridgePanel.ShowFromOverlay())
                {
                    _operationError = "曲库暂不可打开";
                    _operationErrorUntil = Time.unscaledTime + 5f;
                }
            });
        UiKit.CreatePillButton(header.transform, "设置", false, UiKit.LineColor, 26f, 54f)
            .onClick.AddListener(() => SettingsPanelUi.Open(window.Root.transform, 0));
        UiKit.CreatePillButton(header.transform, "关闭", false, UiKit.LineColor, 26f, 54f)
            .onClick.AddListener(() => SaveOverlayOption("MiniPlayerVisible", false));
        var info = UiKit.CreateRow(window.Root.transform, "Current", 48f, 8f);
        window.Cover = UiKit.CreateSquareCover(info.transform, 44f);
        var labels = UiKit.CreateColumn(info.transform, "TrackLabels", 1f);
        window.Title = UiKit.CreateText(labels.transform, "未播放", 14f, TextAnchor.MiddleLeft);
        window.Title.fontStyle = FontStyles.Bold;
        window.Artist = UiKit.CreateText(labels.transform, "", 12f, TextAnchor.MiddleLeft);
        window.Artist.color = UiKit.TextSecondary;
        var actions = UiKit.CreateRow(window.Root.transform, "Transport", 30f, 5f);
        window.Previous = UiKit.CreatePillButton(actions.transform, "上一首", false, UiKit.LineColor, 28f, 62f);
        window.Previous.onClick.AddListener(MusicTransport.Previous);
        window.Play = UiKit.CreatePillButton(actions.transform, "播放", true, UiKit.NeteaseAccent, 28f, 62f);
        window.PlayLabel = window.Play.GetComponentInChildren<TextMeshProUGUI>();
        window.Play.onClick.AddListener(MusicTransport.TogglePlayPause);
        window.Next = UiKit.CreatePillButton(actions.transform, "下一首", false, UiKit.LineColor, 28f, 62f);
        window.Next.onClick.AddListener(MusicTransport.Next);
        window.Favorite = NeteaseFavoriteButton.Create(actions.transform);
        var volume = UiKit.CreateRow(window.Root.transform, "Volume", 22f, 5f);
        UiKit.CreateText(volume.transform, "音量", 11f, TextAnchor.MiddleLeft);
        window.Volume = UiKit.CreateBarSlider(volume.transform, true, 110f);
        window.Volume.onValueChanged.AddListener(MusicTransport.SetVolume);
        UiKit.AddPressCallbacks(window.Volume.gameObject,
            () => window.VolumeDragging = true, () => window.VolumeDragging = false);
        var progress = UiKit.CreateRow(window.Root.transform, "Progress", 24f, 5f);
        window.Progress = UiKit.CreateBarSlider(progress.transform, false);
        window.Time = UiKit.CreateText(progress.transform, "--:-- / --:--", 11f, TextAnchor.MiddleRight);
        window.Time.GetComponent<LayoutElement>().preferredWidth = 94f;
        UiKit.AddPressCallbacks(window.Progress.gameObject,
            () => { var s = PlaybackSnapshotService.Capture(); window.ScrubEpoch = s.OwnerEpoch;
                window.ScrubTrackKey = s.TrackKey; window.Scrubbing = s.CanSeek; },
            () => { if (!window.Scrubbing) return; window.Scrubbing = false;
                var s = PlaybackSnapshotService.Capture();
                if (s.CanSeek && s.OwnerEpoch == window.ScrubEpoch && s.TrackKey == window.ScrubTrackKey)
                    MusicTransport.SeekNormalized(window.Progress.value);
            });
        Place(window, false);
        return window;
    }

    private static void UpdateLyrics(Window window, PlaybackSnapshot playback, OverlayOptions options)
    {
        Color background = UiKit.DockOpaque;
        background.a = _opacityPreview ?? options.BackgroundOpacity;
        if (window.Background.color != background) window.Background.color = background;
        string header = "歌词 · " + BridgePanel.ProviderName(playback.Owner) +
            (options.LyricsLocked ? " · 已锁定" : "") +
            (Time.unscaledTime < _operationErrorUntil ? " · " + _operationError : "") +
            (OverlayLayoutStore.LastError == null ? "" : " · " + OverlayLayoutStore.LastError);
        if (window.Header.text != header) window.Header.text = header;
        if (window.LockLabel.text != (options.LyricsLocked ? "解锁" : "锁定"))
            window.LockLabel.text = options.LyricsLocked ? "解锁" : "锁定";
        float originalSize = 16f * (_fontPreview ?? options.LyricsFontScale);
        float translatedSize = 13f * (_fontPreview ?? options.LyricsFontScale);
        if (Mathf.Abs(window.Original.fontSize - originalSize) > 0.01f) window.Original.fontSize = originalSize;
        if (Mathf.Abs(window.Translation.fontSize - translatedSize) > 0.01f) window.Translation.fontSize = translatedSize;
        string text, translation = "";
        if (string.IsNullOrEmpty(playback.TrackKey)) text = "歌词：未播放";
        else if (!playback.SupportsLyrics) text = "此音源不提供歌词";
        else
        {
            LyricSnapshot lyric = LyricsEngine.Snapshot(playback.Position);
            bool matches = playback.Owner == MusicProvider.Netease ?
                lyric.ContextKey == playback.TrackKey :
                playback.Owner == MusicProvider.AppleMusic && AppleMusicService.NowPlaying != null &&
                lyric.ContextKey == AppleMusicService.NowPlaying.Title + "\u0001" +
                    AppleMusicService.NowPlaying.AlbumTitle + "\u0001" + AppleMusicService.NowPlaying.Artist;
            if (!matches) text = "歌词加载中…";
            else if (lyric.State == LyricsState.Ready)
            {
                text = lyric.OriginalText ?? "";
                translation = options.ShowTranslation ? lyric.TranslationText ?? "" : "";
            }
            else text = lyric.StatusText ?? "暂无歌词";
        }
        if (options.HideLyricsWhenUnavailable && string.IsNullOrEmpty(translation) &&
            (text == "此音源不提供歌词" || text.Contains("暂无歌词") || text.Contains("纯音乐")))
            text = "歌词暂不可用";
        if (window.Original.text != text) window.Original.text = text;
        if (window.Translation.text != translation) window.Translation.text = translation;
    }

    private static void UpdateMini(Window window, PlaybackSnapshot playback, OverlayOptions options)
    {
        Color background = UiKit.DockOpaque;
        background.a = _opacityPreview ?? options.BackgroundOpacity;
        if (window.Background.color != background) window.Background.color = background;
        string header = "迷你播放 · " + BridgePanel.ProviderName(playback.Owner) +
            (options.MiniPlayerLocked ? " · 已锁定" : "") +
            (Time.unscaledTime < _operationErrorUntil ? " · " + _operationError : "") +
            (OverlayLayoutStore.LastError == null ? "" : " · " + OverlayLayoutStore.LastError);
        if (window.Header.text != header) window.Header.text = header;
        if (window.LockLabel.text != (options.MiniPlayerLocked ? "解锁" : "锁定"))
            window.LockLabel.text = options.MiniPlayerLocked ? "解锁" : "锁定";
        if (window.TrackKey != playback.TrackKey)
        {
            window.TrackKey = playback.TrackKey;
            window.Cover.sprite = null; window.Cover.color = UiKit.CoverPlaceholder;
            window.Title.text = string.IsNullOrEmpty(playback.TrackKey) ? "未播放" : "正在读取曲目…";
            if (!string.IsNullOrEmpty(playback.TrackKey)) MusicModules.Current.ApplyCover(window.Cover);
            window.Favorite.Bind(playback.Owner == MusicProvider.Netease && AudioPlayer.Instance?.CurrentTrack != null ?
                AudioPlayer.Instance.CurrentTrack.Id : 0);
        }
        string title = string.IsNullOrEmpty(playback.TrackKey) ? "未播放" : (playback.Title ?? "未知曲目");
        if (window.Title.text != title) window.Title.text = title;
        string artist = playback.Artist ?? "";
        if (window.Artist.text != artist) window.Artist.text = artist;
        bool favoriteVisible = playback.Owner == MusicProvider.Netease && !string.IsNullOrEmpty(playback.TrackKey);
        if (window.Favorite.gameObject.activeSelf != favoriteVisible)
            window.Favorite.gameObject.SetActive(favoriteVisible);
        if (window.Previous.interactable != playback.CanPrevious) window.Previous.interactable = playback.CanPrevious;
        if (window.Next.interactable != playback.CanNext) window.Next.interactable = playback.CanNext;
        bool canPlayPause = !string.IsNullOrEmpty(playback.TrackKey);
        if (window.Play.interactable != canPlayPause) window.Play.interactable = canPlayPause;
        string playLabel = playback.DesiredPlaying ? "暂停" : "播放";
        if (window.PlayLabel.text != playLabel) window.PlayLabel.text = playLabel;
        if (!window.Scrubbing)
        {
            float progress = playback.Duration > 0 ? Mathf.Clamp01((float)(playback.Position / playback.Duration)) : 0f;
            if (Mathf.Abs(window.Progress.value - progress) > 0.0005f)
                window.Progress.SetValueWithoutNotify(progress);
        }
        if (window.Progress.interactable != playback.CanSeek) window.Progress.interactable = playback.CanSeek;
        bool canSetVolume = playback.Volume >= 0;
        if (window.Volume.interactable != canSetVolume) window.Volume.interactable = canSetVolume;
        if (canSetVolume && !window.VolumeDragging && Mathf.Abs(window.Volume.value - playback.Volume) > 0.001f)
            window.Volume.SetValueWithoutNotify(playback.Volume);
        string time = playback.Duration > 0 ? Format(playback.Position) + " / " + Format(playback.Duration) : "--:-- / --:--";
        if (window.Time.text != time) window.Time.text = time;
    }

    private static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--";
        int value = (int)seconds;
        return value / 60 + ":" + (value % 60).ToString("00");
    }

    private static void SaveOverlayOption(string field, bool value)
    {
        if (!MusicBridgeOptions.CanSave) return;
        var baseline = MusicBridgeOptions.Store.Capture(MusicBridgeOptions.Current);
        MusicBridgeOptions.Store.SavePatchAsync(baseline,
            new JObject { ["Overlay"] = new JObject { [field] = value } },
            result =>
            {
                if (result.Success) MusicBridgeOptions.PublishIfCurrent(result);
                else
                {
                    _operationError = "设置未保存";
                    _operationErrorUntil = Time.unscaledTime + 5f;
                    BridgeLog.Warn("悬浮窗设置未保存：" + result.Error);
                }
            });
    }
}
