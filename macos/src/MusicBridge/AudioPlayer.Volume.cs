using UnityEngine;

namespace MusicBridge;

// Keep the actual AudioSource -> preference-store wiring independently testable.
internal sealed partial class AudioPlayer
{
    private PlayerVolumeStore _volumeStore;
    private string _lastVolumeError;

    public float Volume
    {
        get => _source != null ? _source.volume : 0f;
        set
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            if (_source != null)
            {
                _source.volume = Mathf.Clamp01(value);
                _volumeStore?.Set(_source.volume, Time.realtimeSinceStartup);
            }
        }
    }

    private void ReportVolumeError()
    {
        string error = _volumeStore?.LastError;
        if (error != null && error != _lastVolumeError) BridgeLog.Warn(error);
        _lastVolumeError = error;
    }
}
