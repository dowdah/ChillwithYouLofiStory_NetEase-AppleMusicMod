using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicBridge;

// Deliberately separate from MusicBridgeOptions: 1.5 rejects unknown main-config fields.
internal sealed class SteamPresenceOptions
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion = CurrentSchemaVersion;
    public bool Enabled;
    public bool ShowMusic = true;
    public bool ShowTimer = true;
    public bool ShareTrackTitle; // Legacy schema-1 key; now explicitly controls both title and artist.

    internal SteamPresenceOptions Clone() => new SteamPresenceOptions
    {
        SchemaVersion = SchemaVersion, Enabled = Enabled, ShowMusic = ShowMusic,
        ShowTimer = ShowTimer, ShareTrackTitle = ShareTrackTitle
    };

    internal bool SameAs(SteamPresenceOptions other) => other != null &&
        SchemaVersion == other.SchemaVersion && Enabled == other.Enabled && ShowMusic == other.ShowMusic &&
        ShowTimer == other.ShowTimer && ShareTrackTitle == other.ShareTrackTitle;

    internal string Serialize()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("不支持此 Steam 状态配置版本");
        return new JObject
        {
            ["SchemaVersion"] = SchemaVersion, ["Enabled"] = Enabled, ["ShowMusic"] = ShowMusic,
            ["ShowTimer"] = ShowTimer, ["ShareTrackTitle"] = ShareTrackTitle
        }.ToString(Formatting.Indented);
    }

    internal static SteamPresenceOptions Parse(string text)
    {
        // Read explicitly: no numeric/string-to-boolean coercion, duplicate fields, or unknown fields.
        using var input = new StringReader(text ?? "");
        using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 2 };
        if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            throw new InvalidDataException("Steam 状态配置必须是 JSON 对象");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new SteamPresenceOptions();
        bool ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.EndObject) { ended = true; break; }
            if (reader.TokenType != JsonToken.PropertyName) throw new InvalidDataException("Steam 状态配置格式无效");
            string key = (string)reader.Value;
            if (!seen.Add(key)) throw new InvalidDataException("Steam 状态配置含重复字段");
            if (!reader.Read()) throw new InvalidDataException("Steam 状态配置不完整");
            if (key == "SchemaVersion")
            {
                if (reader.TokenType != JsonToken.Integer || !Equals(reader.Value, (long)CurrentSchemaVersion))
                    throw new InvalidDataException("不支持此 Steam 状态配置版本");
                continue;
            }
            if (key != "Enabled" && key != "ShowMusic" && key != "ShowTimer" && key != "ShareTrackTitle")
                throw new InvalidDataException("Steam 状态配置含未知字段");
            if (reader.TokenType != JsonToken.Boolean) throw new InvalidDataException("Steam 状态开关必须为布尔值");
            bool value = (bool)reader.Value;
            switch (key)
            {
                case "Enabled": result.Enabled = value; break;
                case "ShowMusic": result.ShowMusic = value; break;
                case "ShowTimer": result.ShowTimer = value; break;
                case "ShareTrackTitle": result.ShareTrackTitle = value; break;
            }
        }
        if (!ended || !seen.Contains("SchemaVersion") || reader.Read())
            throw new InvalidDataException("Steam 状态配置版本缺失或格式无效");
        return result;
    }
}
