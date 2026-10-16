using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicBridge;

// Separate runtime preference: do not add fields to the strict schema-2 main configuration.
// Only the mod-owned NetEase AudioSource uses this; Music.app and game volume remain independent.
internal sealed class PlayerVolumeStore
{
    internal const float DefaultVolume = 0.7f;
    private readonly string _path;
    private readonly Action<string, string> _write;
    private readonly object _gate = new object(), _saveGate = new object();
    private float _volume = DefaultVolume;
    private string _diskText;
    private bool _canSave = true, _pending, _worker, _closed;
    private int _revision;
    private double _due;
    internal volatile string LastError;
    internal float Volume { get { lock (_gate) return _volume; } }

    internal PlayerVolumeStore(string path, Action<string, string> write = null)
    {
        _path = path;
        _write = write ?? AtomicFile.WriteAllText;
        try
        {
            BridgePaths.ValidateWritePath(path);
            if (File.Exists(path))
            {
                _diskText = File.ReadAllText(path);
                _volume = Parse(_diskText);
            }
        }
        catch (Exception ex)
        {
            _canSave = false; // Preserve rejected/future configuration, never replace it with defaults.
            LastError = "音量配置未加载，保留原文件（" + ex.GetType().Name + "）";
        }
    }

    internal void Set(float value, double now)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return;
        value = Math.Max(0f, Math.Min(1f, value));
        lock (_gate)
        {
            if (_closed || _volume == value) return;
            _volume = value;
            _revision++;
            _pending = true;
            _due = now + 0.5; // Coalesce slider drags without blocking the Unity/audio thread on disk writes.
        }
    }

    internal void Tick(double now)
    {
        lock (_gate)
        {
            if (_closed || !_canSave || !_pending || _worker || now < _due) return;
            _worker = true;
            _due = now + 5; // Failed writes retry at most every five seconds.
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Flush(); }
            finally { lock (_gate) _worker = false; }
        });
    }

    internal void Close()
    {
        lock (_gate) _closed = true;
        Flush(); // Serialize with any queued writer and persist even an immediate adjust-then-quit.
    }

    internal void Flush()
    {
        lock (_saveGate)
        {
            float volume; int revision;
            lock (_gate)
            {
                if (!_canSave || !_pending) return;
                volume = _volume; revision = _revision;
            }
            try
            {
                BridgePaths.ValidateWritePath(_path);
                string current = File.Exists(_path) ? File.ReadAllText(_path) : null;
                if (current != _diskText)
                {
                    lock (_gate) _canSave = false;
                    LastError = "音量配置已被外部修改，未覆盖；重启后重新读取";
                    return;
                }
                string text = new JObject { ["SchemaVersion"] = 1, ["NeteaseVolume"] = volume }
                    .ToString(Formatting.Indented);
                _write(_path, text);
                _diskText = text;
                lock (_gate) if (revision == _revision) _pending = false;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = "音量未保存（" + ex.GetType().Name + "）";
            }
        }
    }

    internal static float Parse(string text)
    {
        using var input = new StringReader(text);
        using var reader = new JsonTextReader(input) { MaxDepth = 2, DateParseHandling = DateParseHandling.None };
        var seen = new HashSet<string>();
        float volume = DefaultVolume;
        if (!reader.Read() || reader.TokenType != JsonToken.StartObject) throw new InvalidDataException();
        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            if (reader.TokenType != JsonToken.PropertyName) throw new InvalidDataException();
            string key = (string)reader.Value;
            if (!seen.Add(key) || !reader.Read()) throw new InvalidDataException();
            if (key == "SchemaVersion")
            {
                if (reader.TokenType != JsonToken.Integer || Convert.ToInt64(reader.Value) != 1)
                    throw new InvalidDataException();
            }
            else if (key == "NeteaseVolume")
            {
                if (reader.TokenType != JsonToken.Float && reader.TokenType != JsonToken.Integer)
                    throw new InvalidDataException();
                double number = Convert.ToDouble(reader.Value);
                if (double.IsNaN(number) || double.IsInfinity(number) || number < 0 || number > 1)
                    throw new InvalidDataException();
                volume = (float)number;
            }
            else throw new InvalidDataException();
        }
        if (reader.TokenType != JsonToken.EndObject || seen.Count != 2 || reader.Read())
            throw new InvalidDataException();
        return volume;
    }
}
