using UnityEngine;

namespace MusicBridge;

internal static class SteamPresenceProbeService
{
    private static readonly SteamPresenceProbe Probe = new SteamPresenceProbe(new SteamPresenceApi(), SteamPresenceProbeCatalog.Profiles);
    internal static string StatusText => Probe.TemplateStatus + "。\n" + Probe.Status + "。\n" + Probe.Observation;
    internal static bool CanPublish => Probe.CanPublish;
    internal static void Inspect() { Probe.Inspect(); Report(); }
    internal static void PublishA() => Probe.Publish(false, Time.realtimeSinceStartupAsDouble);
    internal static void PublishB() => Probe.Publish(true, Time.realtimeSinceStartupAsDouble);
    internal static void PublishStatusA() { Probe.PublishStatus(false, Time.realtimeSinceStartupAsDouble); Report(); }
    internal static void PublishStatusB() { Probe.PublishStatus(true, Time.realtimeSinceStartupAsDouble); Report(); }
    internal static void Stop() { Probe.Stop(Time.realtimeSinceStartupAsDouble); Report(); }
    internal static void Tick()
    {
        string previous = Probe.Status;
        Probe.Tick(Time.realtimeSinceStartupAsDouble);
        if (previous != Probe.Status) Report();
    }
    internal static void Shutdown() => Probe.Shutdown();
    private static void Report() => BridgeLog.Info("[SteamPresenceProbe] " + StatusText.Replace("\n", " | "));
}
