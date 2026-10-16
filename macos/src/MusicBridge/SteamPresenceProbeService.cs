using UnityEngine;

namespace MusicBridge;

internal static class SteamPresenceProbeService
{
    private static readonly SteamPresenceProbe Probe = new SteamPresenceProbe(new SteamPresenceApi(), SteamPresenceProbeCatalog.Profiles);
    internal static string StatusText => Probe.TemplateStatus + "。\n" + Probe.Status + "。\n" + Probe.Observation;
    internal static bool CanPublish => Probe.CanPublish;
    internal static void Inspect() => Probe.Inspect();
    internal static void PublishA() => Probe.Publish(false, Time.realtimeSinceStartupAsDouble);
    internal static void PublishB() => Probe.Publish(true, Time.realtimeSinceStartupAsDouble);
    internal static void Stop() => Probe.Stop(Time.realtimeSinceStartupAsDouble);
    internal static void Tick() => Probe.Tick(Time.realtimeSinceStartupAsDouble);
    internal static void Shutdown() => Probe.Shutdown();
}
