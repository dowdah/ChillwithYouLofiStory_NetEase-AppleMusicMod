using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicBridge;

// The reusable button reads its current binding, never a recycled row's old closure.
internal sealed class NeteaseFavoriteButton : MonoBehaviour
{
    private long _id;
    private Button _button;
    private TextMeshProUGUI _label;
    private float _next;
    public static NeteaseFavoriteButton Create(Transform parent, long id = 0)
    {
        var button = UiKit.CreatePillButton(parent, "状态未知", false, UiKit.LineColor, 24f, 116f);
        var binding = button.gameObject.AddComponent<NeteaseFavoriteButton>();
        binding._button = button; binding._label = button.GetComponentInChildren<TextMeshProUGUI>();
        button.onClick.AddListener(() => { var favorites = NeteaseRuntime.Favorites; favorites.SetLiked(binding._id, !favorites.Target(binding._id)); });
        binding.Bind(id); return binding;
    }
    public void Bind(long id) { _id = id; Refresh(); }
    private void Update() { if (Time.unscaledTime >= _next) { _next = Time.unscaledTime + 0.2f; Refresh(); } }
    private void Refresh()
    {
        if (_button == null) return;
        var favorites = NeteaseRuntime.Favorites;
        _button.interactable = _id > 0 && favorites.Known && NeteaseRuntime.Context != null && NeteaseRuntime.Context.Active;
        string label = !favorites.Known ? "状态未知" : favorites.Target(_id) ? "取消喜欢" : "喜欢";
        if (favorites.Pending(_id)) label += " · 同步中";
        else if (favorites.SongError(_id) != null) label += " · 未确认";
        if (_label != null && _label.text != label) _label.text = label;
    }
}
