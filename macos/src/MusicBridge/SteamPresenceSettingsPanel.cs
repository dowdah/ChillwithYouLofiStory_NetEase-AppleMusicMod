using System;
using System.Collections.Generic;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicBridge;

// Independent draft and lifetime: never serializes the main settings object.
internal sealed class SteamPresenceSettingsPanel : MonoBehaviour
{
    private static SteamPresenceSettingsPanel _instance;
    private SteamPresenceSettingsStore _store;
    private SteamPresenceSettingsDraft _draft;
    private Action<SteamPresenceOptions> _apply;
    private Func<SteamPresenceOptions, string> _preview;
    private Func<string> _diagnostics;
    private Action _closed;
    private RectTransform _rect, _canvas;
    private TextMeshProUGUI _status, _previewText, _diagnosticText;
    private Button _save;
    private GameObject _confirm;
    private readonly List<Action> _refreshToggles = new List<Action>();
    private readonly List<Button> _editButtons = new List<Button>();
    private bool _saving;
    private float _refreshAt;

    internal static bool FocusIfOpen()
    {
        if (_instance == null) return false;
        _instance.gameObject.SetActive(true);
        _instance.transform.SetAsLastSibling();
        return true;
    }

    internal static void Open(Transform from, SteamPresenceSettingsStore store,
        Action<SteamPresenceOptions> apply, Func<SteamPresenceOptions, string> preview,
        Func<string> diagnostics, Action retry = null, Action closed = null)
    {
        if (FocusIfOpen()) return;
        var canvas = from.GetComponentInParent<Canvas>();
        if (canvas == null) { BridgeLog.Warn("未找到游戏 Canvas，Steam 状态设置无法显示。"); return; }
        var root = new GameObject("MusicBridgeSteamPresenceSettings");
        root.transform.SetParent(canvas.transform, false);
        root.transform.SetAsLastSibling();
        _instance = root.AddComponent<SteamPresenceSettingsPanel>();
        _instance._store = store; _instance._apply = apply; _instance._preview = preview;
        _instance._diagnostics = diagnostics; _instance._closed = closed;
        // Opening explicitly reloads this independent file; corrupt/future files disable publishing.
        var loaded = store.Read();
        try { apply?.Invoke(loaded); }
        catch (Exception ex) { BridgeLog.Warn("Steam 状态配置应用失败（" + ex.GetType().Name + "）"); }
        _instance._draft = store.Capture();
        _instance.Build(canvas.transform as RectTransform, retry);
    }

    private void Build(RectTransform canvas, Action retry)
    {
        _canvas = canvas;
        _rect = gameObject.AddComponent<RectTransform>();
        _rect.anchorMin = _rect.anchorMax = _rect.pivot = new Vector2(0.5f, 0.5f);
        Resize();
        var image = gameObject.AddComponent<Image>();
        image.sprite = UiSprites.Rounded; image.type = Image.Type.Sliced; image.color = UiKit.DockOpaque;
        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 10, 10); layout.spacing = 6f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        var header = UiKit.CreateRow(transform, "Title", 36f, 8f);
        var title = UiKit.CreateText(header.transform, "Steam 好友状态", 18f, TextAnchor.MiddleLeft);
        title.fontStyle = FontStyles.Bold; FillLabel(title, 32f);
        Button(header.transform, "关闭", false, 68f).onClick.AddListener(RequestClose);

        var viewport = UiKit.NewRect("PresenceViewport", transform);
        var viewportSize = viewport.gameObject.AddComponent<LayoutElement>();
        viewportSize.flexibleHeight = 1f; viewportSize.minHeight = 40f;
        viewport.gameObject.AddComponent<Image>().color = Color.clear;
        viewport.gameObject.AddComponent<RectMask2D>();
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = viewport;
        var body = UiKit.CreateColumn(viewport, "PresenceContent", 8f, new RectOffset(4, 16, 4, 8));
        var content = body.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f); content.sizeDelta = Vector2.zero;
        scroll.content = content;
        AddScrollbar(viewport, scroll);
        Note(body.transform, "仅显示于好友右键菜单的“查看游戏信息 → 状态”，不会显示在好友列表主行。与 macOS 媒体控制独立。", 66f);
        Toggle(body.transform, "启用 Steam 好友状态", () => _draft.Working.Enabled, v => _draft.Working.Enabled = v);
        Toggle(body.transform, "显示音乐播放状态", () => _draft.Working.ShowMusic, v => _draft.Working.ShowMusic = v);
        Toggle(body.transform, "显示计时器状态", () => _draft.Working.ShowTimer, v => _draft.Working.ShowTimer = v);
        Toggle(body.transform, "向好友公开当前曲名", () => _draft.Working.ShareTrackTitle, v => _draft.Working.ShareTrackTitle = v);
        Note(body.transform, "默认关闭且默认不公开曲名。音乐状态和可选曲名在前，计时器在后。保存后生效；预览不会发布，更新有节流及 Steam 传播延迟。", 66f);
        Note(body.transform, "本机预览（当前草稿）", 28f);
        _previewText = Note(body.transform, "", 84f);
        _previewText.richText = false;
        Note(body.transform, "运行诊断（不包含曲名和账号）", 28f);
        _diagnosticText = Note(body.transform, "", 170f);
        if (retry != null)
        {
            var retryButton = Button(body.transform, "手动重试发布", false);
            retryButton.onClick.AddListener(() =>
            {
                try { retry(); _refreshAt = 0; }
                catch (Exception ex) { _status.text = "重试失败（" + ex.GetType().Name + "）"; }
            });
        }
        _status = Note(transform, _store.CanSave ? "设置独立保存；取消不会影响当前发布。" : _store.Error, 54f);
        var actions = UiKit.CreateRow(transform, "Actions", 32f, 8f);
        _save = Button(actions.transform, "保存", true); _save.onClick.AddListener(() => Save(false));
        Button(actions.transform, "取消", false).onClick.AddListener(RequestClose);
        var defaults = Button(transform, "恢复默认到草稿", false);
        _editButtons.Add(defaults);
        defaults.onClick.AddListener(() =>
        {
            _draft.ResetDefaults(); foreach (var refresh in _refreshToggles) refresh(); Changed();
        });
        _confirm = UiKit.CreateColumn(transform, "UnsavedPresenceConfirmation", 4f);
        _confirm.AddComponent<LayoutElement>().preferredHeight = 64f;
        var confirmActions = UiKit.CreateRow(_confirm.transform, "UnsavedActions", 30f, 6f);
        Button(confirmActions.transform, "保存并关闭", true).onClick.AddListener(() => Save(true));
        Button(confirmActions.transform, "放弃修改", false).onClick.AddListener(Close);
        Button(_confirm.transform, "继续编辑", false).onClick.AddListener(() => _confirm.SetActive(false));
        _confirm.SetActive(false);
        RefreshButtons(); RefreshText();
    }

    private void Toggle(Transform parent, string label, Func<bool> get, Action<bool> set)
    {
        var row = UiKit.CreateRow(parent, "PresenceToggle", 48f, 8f, TextAnchor.MiddleLeft, new RectOffset(12, 12, 0, 0));
        var image = row.AddComponent<Image>();
        image.sprite = UiSprites.Rounded; image.type = Image.Type.Sliced;
        image.color = UiKit.SettingsRowTint; image.raycastTarget = false;
        var description = UiKit.CreateText(row.transform, label, 13f, TextAnchor.MiddleLeft);
        FillLabel(description, 42f);
        var button = Button(row.transform, "", false, 64f); _editButtons.Add(button);
        var text = button.GetComponentInChildren<TextMeshProUGUI>();
        void Refresh() { text.text = get() ? "开" : "关"; }
        _refreshToggles.Add(Refresh); Refresh();
        button.onClick.AddListener(() => { set(!get()); Refresh(); Changed(); });
    }

    private static Button Button(Transform parent, string text, bool filled, float width = -1)
    {
        var button = UiKit.CreatePillButton(parent, text, filled, UiKit.NeteaseAccent, 30f, width);
        if (width < 0)
        {
            var size = button.GetComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = 0f; size.flexibleWidth = 1f;
        }
        var label = button.GetComponentInChildren<TextMeshProUGUI>();
        label.enableAutoSizing = true; label.fontSizeMin = 10f; label.fontSizeMax = UiKit.GameArtistFontSize;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return button;
    }

    private static TextMeshProUGUI Note(Transform parent, string text, float height)
    {
        var label = UiKit.CreateText(parent, text, 12f, TextAnchor.UpperLeft);
        FillLabel(label, height); label.color = UiKit.TextSecondary;
        return label;
    }

    private static void FillLabel(TextMeshProUGUI label, float height)
    {
        var size = label.GetComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = 0; size.flexibleWidth = 1;
        size.minHeight = size.preferredHeight = height;
        label.enableWordWrapping = true; label.overflowMode = TextOverflowModes.Ellipsis;
    }

    private static void AddScrollbar(RectTransform viewport, ScrollRect scroll)
    {
        var track = UiKit.NewRect("Scrollbar", viewport);
        track.anchorMin = new Vector2(1f, 0f); track.anchorMax = Vector2.one;
        track.pivot = new Vector2(1f, 0.5f); track.sizeDelta = new Vector2(6f, -8f);
        track.anchoredPosition = new Vector2(-3f, 0);
        track.gameObject.AddComponent<Image>().color = UiKit.LineSoft;
        var handle = UiKit.NewRect("Handle", track);
        handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one;
        handle.offsetMin = handle.offsetMax = Vector2.zero;
        var graphic = handle.gameObject.AddComponent<Image>(); graphic.color = UiKit.LineColor;
        var bar = track.gameObject.AddComponent<Scrollbar>();
        bar.direction = Scrollbar.Direction.BottomToTop; bar.handleRect = handle; bar.targetGraphic = graphic;
        scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
    }

    private void Changed()
    {
        _status.text = "有未保存修改；保存后生效，取消将丢弃草稿。";
        RefreshButtons(); RefreshText();
    }

    private void RefreshButtons()
    {
        _save.interactable = !_saving && _store.CanSave && _draft.Dirty;
        foreach (var button in _editButtons) button.interactable = !_saving && _store.CanSave;
    }

    private void RefreshText()
    {
        try { _previewText.text = _preview?.Invoke(_draft.Working.Clone()) ?? "无可展示内容"; }
        catch { _previewText.text = "预览暂不可用"; }
        try { _diagnosticText.text = _diagnostics?.Invoke() ?? "诊断暂不可用"; }
        catch { _diagnosticText.text = "诊断暂不可用"; }
        FitWrappedText(_previewText, 84f);
        FitWrappedText(_diagnosticText, 170f);
    }

    private void FitWrappedText(TextMeshProUGUI text, float minimum)
    {
        float width = text.rectTransform.rect.width;
        if (width < 1f) width = Mathf.Max(80f, _rect.sizeDelta.x - 60f);
        float height = Mathf.Max(minimum, Mathf.Ceil(text.GetPreferredValues(text.text, width, float.PositiveInfinity).y) + 8f);
        var size = text.GetComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = height;
        text.overflowMode = TextOverflowModes.Overflow;
    }

    private void Save(bool close)
    {
        if (_saving || !_store.CanSave) return;
        if (!_draft.Dirty) { if (close) Close(); return; }
        _saving = true; _status.text = "正在保存独立配置…"; RefreshButtons();
        var working = _draft.Working.Clone(); var baseline = _draft;
        var store = _store; var apply = _apply;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var result = store.Save(baseline, working);
            MainThreadDispatcher.Enqueue(() =>
            {
                bool applied = false;
                if (store.IsCurrent(result))
                {
                    try { apply?.Invoke(result.Options.Clone()); applied = true; }
                    catch (Exception ex) { result.Error = "配置已保存，但应用失败（" + ex.GetType().Name + "），请手动重试"; }
                }
                if (_instance != this) return;
                _saving = false;
                if (result.Success) _draft = store.Capture();
                _status.text = result.Error ?? (applied ? "已保存。是否已提交至 Steam 请查看运行诊断。" : "配置随后发生变化，请重新打开设置。");
                _confirm.SetActive(false); RefreshButtons(); RefreshText();
                if (applied && close) Close();
            });
        });
    }

    private void RequestClose()
    {
        if (_saving) return;
        if (_draft.Dirty) { _confirm.SetActive(true); return; }
        Close();
    }

    private void Close()
    {
        if (_saving) return;
        var closed = _closed; _closed = null;
        _instance = null; Destroy(gameObject); closed?.Invoke();
    }

    private void Resize()
    {
        if (_rect == null || _canvas == null) return;
        _rect.sizeDelta = new Vector2(Mathf.Min(680, Mathf.Max(0, _canvas.rect.width - 32)),
            Mathf.Min(580, Mathf.Max(0, _canvas.rect.height - 32)));
    }

    private void Update()
    {
        Resize();
        if (Time.unscaledTime >= _refreshAt) { _refreshAt = Time.unscaledTime + 0.5f; RefreshText(); }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_confirm.activeSelf) _confirm.SetActive(false); else RequestClose();
        }
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }
}
