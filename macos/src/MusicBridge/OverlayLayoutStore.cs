using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace MusicBridge;

internal sealed class OverlayLayoutState
{
    public int LayoutSchemaVersion = 1;
    public float LyricsX = 0.72f, LyricsY = 0.72f;
    public float MiniX = 0.72f, MiniY = 0.21f;
}

internal static class OverlayLayoutStore
{
    private static readonly object Gate = new object();
    private static readonly object SaveGate = new object();
    private static OverlayLayoutState _current;
    private static int _revision;
    internal static volatile string LastError;
    private static string Path => BridgePaths.Resolve("config", "musicbridge.layout.json");

    internal static OverlayLayoutState Current
    {
        get
        {
            lock (Gate)
            {
                if (_current != null) return Clone(_current);
                try
                {
                    if (!File.Exists(Path)) _current = new OverlayLayoutState();
                    else
                    {
                        _current = JsonConvert.DeserializeObject<OverlayLayoutState>(File.ReadAllText(Path));
                        Validate(_current);
                    }
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("悬浮窗布局不可读取，已使用默认位置（" + ex.GetType().Name + "）。");
                    _current = new OverlayLayoutState();
                }
                return Clone(_current);
            }
        }
    }

    internal static void Set(bool lyrics, float x, float y)
    {
        if (float.IsNaN(x) || float.IsNaN(y)) return;
        lock (Gate)
        {
            var next = Current;
            if (lyrics) { next.LyricsX = Math.Max(0, Math.Min(1, x)); next.LyricsY = Math.Max(0, Math.Min(1, y)); }
            else { next.MiniX = Math.Max(0, Math.Min(1, x)); next.MiniY = Math.Max(0, Math.Min(1, y)); }
            _current = next;
            SaveAsync(Clone(next), ++_revision);
        }
    }

    internal static void Reset()
    {
        lock (Gate) { _current = new OverlayLayoutState(); SaveAsync(Clone(_current), ++_revision); }
    }

    private static void SaveAsync(OverlayLayoutState snapshot, int revision)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (SaveGate)
                {
                    lock (Gate) if (revision != _revision) return;
                    AtomicFile.WriteAllText(Path, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                    LastError = null;
                }
            }
            catch (Exception ex)
            {
                LastError = "布局未保存";
                BridgeLog.Warn("悬浮窗布局保存失败（" + ex.GetType().Name + "）。");
            }
        });
    }

    private static OverlayLayoutState Clone(OverlayLayoutState value) => new OverlayLayoutState
    { LyricsX = value.LyricsX, LyricsY = value.LyricsY, MiniX = value.MiniX, MiniY = value.MiniY };

    private static void Validate(OverlayLayoutState value)
    {
        if (value == null || value.LayoutSchemaVersion != 1 ||
            float.IsNaN(value.LyricsX) || float.IsNaN(value.LyricsY) || float.IsNaN(value.MiniX) || float.IsNaN(value.MiniY) ||
            value.LyricsX < 0 || value.LyricsX > 1 || value.LyricsY < 0 || value.LyricsY > 1 ||
            value.MiniX < 0 || value.MiniX > 1 || value.MiniY < 0 || value.MiniY > 1)
            throw new InvalidDataException("布局内容无效");
    }
}
