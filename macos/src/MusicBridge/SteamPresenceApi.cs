using System;
using System.Collections.Generic;
using System.Reflection;

namespace MusicBridge;

internal interface ISteamPresenceApi
{
    // Session is private in-memory identity, never diagnostic output.
    bool TryConnect(out string session, out string error);
    Dictionary<string, string> ReadOwnPresence();
    bool Set(string key, string value);
}

// Uses only the game's already-loaded wrapper and initialized Steam user handle.
// No Init, Shutdown, RunCallbacks, ClearRichPresence, assembly loading or native SDK copy.
internal sealed class SteamPresenceApi : ISteamPresenceApi
{
    internal const uint ExpectedAppId = 3548580;
    private readonly Func<string, Type> _resolve;
    private MethodInfo _handle, _loggedOn, _user, _app, _count, _key, _read, _set;
    private FieldInfo _handleValue, _appValue, _userValue;
    private object _currentUser;
    private bool _bound;

    internal SteamPresenceApi() : this(ResolveLoadedType) { }
    internal SteamPresenceApi(Func<string, Type> resolve) { _resolve = resolve; }

    private static Type ResolveLoadedType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name == "com.rlabrecque.steamworks.net")
                return assembly.GetType("Steamworks." + name, false);
        return null;
    }

    private Type Type(string name) => _resolve(name) ?? throw new MissingMemberException(name);
    private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null, args, null);
        if (method == null || method.ReturnType != result) throw new MissingMethodException(type.Name, name);
        return method;
    }
    private static FieldInfo Field(Type type, string name, Type result)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null || field.FieldType != result) throw new MissingFieldException(type.Name, name);
        return field;
    }
    private void Bind()
    {
        if (_bound) return;
        var api = Type("SteamAPI"); var user = Type("SteamUser"); var friends = Type("SteamFriends");
        var id = Type("CSteamID"); var app = Type("AppId_t"); var handle = Type("HSteamUser");
        _handle = Method(api, "GetHSteamUser", handle);
        _handleValue = Field(handle, "m_HSteamUser", typeof(int));
        _loggedOn = Method(user, "BLoggedOn", typeof(bool));
        _user = Method(user, "GetSteamID", id);
        _userValue = Field(id, "m_SteamID", typeof(ulong));
        _app = Method(Type("SteamUtils"), "GetAppID", app);
        _appValue = Field(app, "m_AppId", typeof(uint));
        _count = Method(friends, "GetFriendRichPresenceKeyCount", typeof(int), id);
        _key = Method(friends, "GetFriendRichPresenceKeyByIndex", typeof(string), id, typeof(int));
        _read = Method(friends, "GetFriendRichPresence", typeof(string), id, typeof(string));
        _set = Method(friends, "SetRichPresence", typeof(bool), typeof(string), typeof(string));
        _bound = true;
    }

    public bool TryConnect(out string session, out string error)
    {
        session = null; error = null; _currentUser = null;
        try
        {
            Bind();
            int handle = (int)_handleValue.GetValue(_handle.Invoke(null, null));
            if (handle == 0) { error = "游戏尚未初始化 Steam"; return false; }
            if (!(bool)_loggedOn.Invoke(null, null)) { error = "Steam 未登录或离线"; return false; }
            uint app = (uint)_appValue.GetValue(_app.Invoke(null, null));
            if (app != ExpectedAppId) { error = "AppID 不匹配，拒绝操作"; return false; }
            var user = _user.Invoke(null, null);
            ulong id = (ulong)_userValue.GetValue(user);
            if (id == 0) { error = "Steam 用户尚未就绪"; return false; }
            _currentUser = user;
            session = app + ":" + id + ":" + handle;
            return true;
        }
        catch (Exception ex) { error = "Steam 接口不可用（" + ErrorKind(ex) + "）"; return false; }
    }

    internal static string ErrorKind(Exception ex) =>
        (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex).GetType().Name;

    public Dictionary<string, string> ReadOwnPresence()
    {
        if (_currentUser == null) throw new InvalidOperationException("Steam not ready");
        int count = (int)_count.Invoke(null, new[] { _currentUser });
        if (count < 0 || count > 64) throw new InvalidOperationException("Unexpected presence key count");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var key = (string)_key.Invoke(null, new object[] { _currentUser, i });
            if (!string.IsNullOrEmpty(key))
                values[key] = (string)_read.Invoke(null, new object[] { _currentUser, key }) ?? "";
        }
        return values;
    }

    public bool Set(string key, string value)
    {
        if (_currentUser == null) throw new InvalidOperationException("Steam not ready");
        return (bool)_set.Invoke(null, new object[] { key, value ?? "" });
    }
}
