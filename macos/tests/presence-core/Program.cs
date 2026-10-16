using System;
using System.Collections.Generic;
using MusicBridge;

internal static class Program
{
    private static int _checks;
    // Synthetic unit-test-only profile. Never included in the actual plugin catalog.
    private static readonly SteamPresenceProbeProfile[] Profiles = {
        new SteamPresenceProbeProfile("#UnitTestOnly", "test_text", "synthetic fixture, not a Steam template")
    };
    private static void Check(bool result, string name)
    { if (!result) throw new Exception(name); _checks++; }
    private static void Main()
    {
        var fake = new Fake();
        var probe = new SteamPresenceProbe(fake, SteamPresenceProbeCatalog.Profiles);
        Check(!probe.CanPublish, "unverified production catalog disables writes");
        probe.Publish(false, 0);
        Check(fake.Connects == 0 && fake.Writes == 0, "empty catalog never touches Steam");
        bool invalidProfile = false;
        try { new SteamPresenceProbeProfile("#Bad\n", "text", "fixture"); } catch (ArgumentException) { invalidProfile = true; }
        Check(invalidProfile, "profile tokens reject trailing control characters");
        fake.Values["private_data"] = "DO_NOT_PRINT";
        fake.Values["steam_display"] = "<b>DO_NOT_PRINT</b>";
        fake.Values["<b>unsafe</b>"] = "x";
        probe.Inspect();
        Check(fake.Writes == 0 && !probe.Observation.Contains("DO_NOT_PRINT") && !probe.Observation.Contains("<b>"), "inspection redacts values and markup");

        fake = new Fake(); fake.Values["untouched"] = "original";
        probe = new SteamPresenceProbe(fake, Profiles);
        probe.Publish(false, 10);
        Check(probe.Active && fake.Values["test_text"] == SteamPresenceProbe.TextA && fake.Values["steam_display"] == "#UnitTestOnly", "publish A");
        Check(fake.Order[0] == "test_text" && fake.Order[1] == "steam_display", "parameter precedes display");
        Check(probe.Status.Contains("好友列表仍待确认"), "readback is not friend verification");
        probe.Publish(true, 20);
        Check(fake.Values["test_text"] == SteamPresenceProbe.TextB, "publish B");
        probe.Stop(21); int writes = fake.Writes;
        Check(fake.Values.Count == 1 && fake.Values["untouched"] == "original", "absent keys removed and unrelated preserved");
        probe.Stop(22); probe.Shutdown();
        Check(fake.Writes == writes, "cleanup idempotent");

        fake = new Fake(); fake.Values["steam_display"] = "#Original"; fake.Values["test_text"] = "old";
        probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0); probe.Publish(true, 1); probe.Stop(2);
        Check(fake.Values["steam_display"] == "#Original" && fake.Values["test_text"] == "old", "original survives A then B");
        probe.Publish(false, 3); fake.Values["steam_display"] = "#GameOwned"; probe.Publish(true, 4);
        Check(!probe.Active && fake.Values["steam_display"] == "#GameOwned" && fake.Values["test_text"] == "old", "external takeover stops and preserves game token");

        fake = new Fake { RejectKey = "steam_display" };
        probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        Check(!probe.Active && fake.Values.Count == 0, "partial write rolls back parameter");
        fake = new Fake { ThrowAfterWrite = true };
        probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        Check(!probe.Active && fake.Values.Count == 0, "uncertain successful write recovered");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        fake.RejectKey = "test_text"; probe.Publish(true, 1);
        Check(probe.Active && fake.Values["test_text"] == SteamPresenceProbe.TextA, "failed B retains ownership of A while rollback blocked");
        fake.RejectKey = null; probe.Tick(6);
        Check(!probe.Active && fake.Values.Count == 0, "retry restores prior A after rejected B");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        fake.Ready = false; probe.Stop(1); writes = fake.Writes;
        Check(probe.Active, "disconnected rollback remains pending");
        probe.Tick(2); Check(fake.Writes == writes, "cleanup retry is throttled");
        fake.Ready = true; probe.Tick(6);
        Check(!probe.Active && fake.Values.Count == 0, "reconnect cleanup");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        fake.Session = "another-user"; fake.Values.Clear(); fake.Values["steam_display"] = "#OtherUser";
        writes = fake.Writes; probe.Stop(1);
        Check(fake.Writes == writes && fake.Values["steam_display"] == "#OtherUser", "never restore across accounts");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        probe.Tick(119); Check(probe.Active, "probe remains before deadline");
        probe.Tick(120); Check(!probe.Active && fake.Values.Count == 0, "timeout without UI restores");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        fake.Values["test_text"] = "game-changed"; probe.Shutdown();
        Check(fake.Values["test_text"] == "game-changed" && !fake.Values.ContainsKey("steam_display"), "exit preserves externally changed value");
        TestStatusOnly();
        TestReflectionAdapter();
        Console.WriteLine("PASS: " + _checks + " Steam presence assertions (fake Steam only)");
    }

    private static void TestStatusOnly()
    {
        var fake = new Fake(); fake.Values["untouched"] = "original";
        var probe = new SteamPresenceProbe(fake, SteamPresenceProbeCatalog.Profiles);
        probe.PublishStatus(false, 0);
        Check(probe.Active && fake.Values["status"] == SteamPresenceProbe.StatusTextA, "status A works without a template");
        Check(fake.Order.TrueForAll(key => key == "status") && !fake.Values.ContainsKey("steam_display"), "status-only never writes template or unrelated keys");
        probe.PublishStatus(true, 1);
        Check(fake.Values["status"] == SteamPresenceProbe.StatusTextB && !fake.Values.ContainsKey("steam_display"), "status B updates without a template");
        probe.Tick(120); Check(probe.Active, "status B extends timeout");
        probe.Tick(121);
        Check(!probe.Active && fake.Values.Count == 1 && fake.Values["untouched"] == "original", "status timeout restores absence and unrelated keys");

        fake = new Fake(); fake.Values["status"] = "old-status";
        probe = new SteamPresenceProbe(fake, Profiles); probe.PublishStatus(false, 0);
        int writes = fake.Writes; probe.Publish(false, 1);
        Check(fake.Writes == writes && !fake.Values.ContainsKey("steam_display"), "template test cannot mix into active status test");
        probe.Stop(2); Check(fake.Values["status"] == "old-status", "status restores original value");

        fake = new Fake(); fake.Values["steam_display"] = "#Original";
        probe = new SteamPresenceProbe(fake, Profiles); probe.PublishStatus(false, 0);
        Check(fake.Writes == 0, "preexisting display prevents pure status experiment");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, Profiles); probe.Publish(false, 0);
        writes = fake.Writes; probe.PublishStatus(false, 1);
        Check(fake.Writes == writes && !fake.Values.ContainsKey("status"), "status cannot mix into template experiment");
        probe.Stop(2);

        fake = new Fake(); probe = new SteamPresenceProbe(fake, SteamPresenceProbeCatalog.Profiles);
        probe.PublishStatus(false, 0); fake.Values["status"] = "external";
        probe.PublishStatus(true, 1);
        Check(!probe.Active && fake.Values["status"] == "external", "status external takeover preserved");

        fake = new Fake(); probe = new SteamPresenceProbe(fake, SteamPresenceProbeCatalog.Profiles);
        probe.PublishStatus(false, 0); fake.Values["steam_display"] = "#NewGameTemplate";
        probe.PublishStatus(true, 1);
        Check(!probe.Active && !fake.Values.ContainsKey("status") && fake.Values["steam_display"] == "#NewGameTemplate", "new external template stops status experiment without touching template");

        fake = new Fake(); fake.Values["status"] = "before";
        probe = new SteamPresenceProbe(fake, SteamPresenceProbeCatalog.Profiles); probe.PublishStatus(false, 0);
        fake.RejectKey = "status"; probe.PublishStatus(true, 1);
        Check(probe.Active, "rejected status B retains pending cleanup");
        fake.RejectKey = null; probe.Tick(6);
        Check(!probe.Active && fake.Values["status"] == "before", "failed status B recovers prior state");
    }

    private static void TestReflectionAdapter()
    {
        var missing = new SteamPresenceApi(_ => null);
        Check(!missing.TryConnect(out _, out _), "missing wrapper degrades");
        var api = new SteamPresenceApi(name => name switch {
            "SteamAPI" => typeof(TestApi), "SteamUser" => typeof(TestUser), "SteamUtils" => typeof(TestUtils),
            "SteamFriends" => typeof(TestFriends), "CSteamID" => typeof(TestId),
            "AppId_t" => typeof(TestApp), "HSteamUser" => typeof(TestHandle), _ => null
        });
        TestApi.Handle = 0; TestUser.Calls = 0;
        Check(!api.TryConnect(out _, out _) && TestUser.Calls == 0, "zero handle prevents context calls");
        TestApi.Handle = 1; TestUser.LoggedOn = false;
        Check(!api.TryConnect(out _, out _), "offline rejected");
        TestUser.LoggedOn = true; TestUtils.App = 480;
        Check(!api.TryConnect(out _, out _), "wrong AppID rejected");
        TestUtils.App = SteamPresenceApi.ExpectedAppId;
        Check(api.TryConnect(out string session, out _) && session != null, "correct initialized session");
        Check(api.Set("test", "value") && api.ReadOwnPresence()["test"] == "value", "cached reflection set and read signatures");
        api.Set("test", null); Check(api.ReadOwnPresence().Count == 0, "null restores absent key");
        TestUser.Fail = true;
        Check(!api.TryConnect(out _, out string error) && !error.Contains("PRIVATE_EXCEPTION"), "native exception data redacted");
        bool threw = false; try { api.Set("test", "value"); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "failed reconnect invalidates cached user");
    }

    private sealed class Fake : ISteamPresenceApi
    {
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal readonly List<string> Order = new List<string>();
        internal int Connects, Writes;
        internal bool Ready = true, ThrowAfterWrite;
        internal string Session = "session", RejectKey;
        public bool TryConnect(out string session, out string error)
        { Connects++; session = Session; error = Ready ? null : "offline"; return Ready; }
        public Dictionary<string, string> ReadOwnPresence() => new Dictionary<string, string>(Values);
        public bool Set(string key, string value)
        {
            Writes++; Order.Add(key);
            if (key == RejectKey) return false;
            if (string.IsNullOrEmpty(value)) Values.Remove(key); else Values[key] = value;
            if (ThrowAfterWrite) { ThrowAfterWrite = false; throw new Exception("uncertain write"); }
            return true;
        }
    }
    public struct TestId { public ulong m_SteamID; }
    public struct TestApp { public uint m_AppId; }
    public struct TestHandle { public int m_HSteamUser; }
    public static class TestApi
    {
        public static int Handle;
        public static TestHandle GetHSteamUser() => new TestHandle { m_HSteamUser = Handle };
    }
    public static class TestUser
    {
        public static bool LoggedOn = true, Fail;
        public static int Calls;
        public static bool BLoggedOn() { Calls++; if (Fail) throw new Exception("PRIVATE_EXCEPTION"); return LoggedOn; }
        public static TestId GetSteamID() => new TestId { m_SteamID = 123 };
    }
    public static class TestUtils
    {
        public static uint App;
        public static TestApp GetAppID() => new TestApp { m_AppId = App };
    }
    public static class TestFriends
    {
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public static bool SetRichPresence(string key, string value)
        { if (string.IsNullOrEmpty(value)) Values.Remove(key); else Values[key] = value; return true; }
        public static int GetFriendRichPresenceKeyCount(TestId _) => Values.Count;
        public static string GetFriendRichPresenceKeyByIndex(TestId _, int index) => new List<string>(Values.Keys)[index];
        public static string GetFriendRichPresence(TestId _, string key) => Values.TryGetValue(key, out var value) ? value : "";
    }
}
