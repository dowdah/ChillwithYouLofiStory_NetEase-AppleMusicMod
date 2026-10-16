using System;

namespace MusicBridge;

// The native slider belongs to the game. Rebaseline on every owner/track-availability
// transition; a value changed while another source was active is not a new volume edit.
internal sealed class GameVolumeRelay
{
    private MusicProvider? _owner;
    private bool _active;
    private float _last;

    internal void Reset() { _owner = null; _active = false; }

    internal bool Observe(IMusicModule current, float sliderValue)
    {
        if (current == null || float.IsNaN(sliderValue) || float.IsInfinity(sliderValue) ||
            sliderValue < 0 || sliderValue > 1)
        { Reset(); return false; }
        bool active = current.Id != MusicProvider.GameBuiltIn && current.HasTrack;
        if (_owner != current.Id || !_active || !active)
        {
            _owner = current.Id; _active = active; _last = sliderValue;
            return false;
        }
        if (Math.Abs(sliderValue - _last) <= 0.001f) return false;
        current.SetVolume(sliderValue);
        _last = sliderValue;
        return true;
    }
}
