using UnityEngine;
using UnityEngine.EventSystems;

namespace MusicBridge;

// Only the title bar receives drag events; controls remain independent.
internal sealed class OverlayDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    internal RectTransform Window;
    internal RectTransform CanvasRect;
    internal Canvas Canvas;
    internal bool Lyrics;
    private bool _dragging;

    public void OnBeginDrag(PointerEventData data)
    {
        _dragging = !Locked();
    }

    public void OnDrag(PointerEventData data)
    {
        if (!_dragging || Window == null || CanvasRect == null) return;
        float scale = Canvas != null && Canvas.scaleFactor > 0 ? Canvas.scaleFactor : 1f;
        Window.anchoredPosition += data.delta / scale;
        Clamp();
    }

    public void OnEndDrag(PointerEventData data)
    {
        if (!_dragging || Window == null || CanvasRect == null) return;
        _dragging = false;
        Clamp();
        var size = CanvasRect.rect.size;
        if (size.x > 0 && size.y > 0)
            OverlayLayoutStore.Set(Lyrics, Window.anchoredPosition.x / size.x + 0.5f,
                Window.anchoredPosition.y / size.y + 0.5f);
    }

    internal void Clamp()
    {
        if (Window == null || CanvasRect == null) return;
        var bounds = CanvasRect.rect.size;
        var size = Window.rect.size;
        const float visibleMargin = 20f;
        float x = Mathf.Clamp(Window.anchoredPosition.x,
            -bounds.x / 2f + size.x / 2f - visibleMargin,
             bounds.x / 2f - size.x / 2f + visibleMargin);
        float y = Mathf.Clamp(Window.anchoredPosition.y,
            -bounds.y / 2f + size.y / 2f - visibleMargin,
             bounds.y / 2f - size.y / 2f + visibleMargin);
        Window.anchoredPosition = new Vector2(x, y);
    }

    private bool Locked() => Lyrics ? MusicBridgeOptions.Current.Overlay.LyricsLocked :
        MusicBridgeOptions.Current.Overlay.MiniPlayerLocked;
}
