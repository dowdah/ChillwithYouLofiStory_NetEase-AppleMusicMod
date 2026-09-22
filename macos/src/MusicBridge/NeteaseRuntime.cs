using System;
using System.Threading;

namespace MusicBridge;

internal static class NeteaseRuntime
{
    public static NeteaseAccountContext Context { get; private set; }
    private static readonly INeteaseClient Client = new NeteaseClient();
    private static void Work(Action action) => ThreadPool.QueueUserWorkItem(_ => action());
    public static readonly NeteaseFavorites Favorites = new NeteaseFavorites(Client, Work, Plugin.RunOnMainThread, () => DateTime.UtcNow);
    public static readonly NeteasePersonalFm Fm = new NeteasePersonalFm(Client, Work, Plugin.RunOnMainThread, () => DateTime.UtcNow);
    static NeteaseRuntime()
    {
        Favorites.Changed += NeteasePanelUi.RefreshEnhancements;
        Favorites.SnapshotChanged += NeteaseLibrary.SyncLikedSnapshot;
        Fm.Changed += NeteasePanelUi.RefreshEnhancements;
        Fm.BatchAccepted += (batch, added) => BridgeLog.Info("私人FM有效批次=" + batch + "，新增=" + added + " 首。");
        Fm.Play += track => {
            if (Context != null && Context.Active && PlaybackCoordinator.Active == MusicProvider.Netease && !Fm.Suspended)
                AudioPlayer.Instance?.PlayFmTrack(track);
        };
        Fm.Wait += () => AudioPlayer.Instance?.WaitForFm();
    }
    public static void Adopt(NeteaseAccountContext context)
    {
        Context?.Invalidate(); Fm.End(); Context = context; Favorites.Bind(context);
    }
    public static void Shutdown()
    { Context?.Invalidate(); Context = null; Fm.End(); Favorites.Bind(null); }
    public static void StartFm()
    {
        if (Context == null || !Context.Active) return;
        PlaybackCoordinator.MarkUserChose(); PlaybackCoordinator.Claim(MusicProvider.Netease);
        Fm.Start(Context);
    }
    public static void ResumeFm()
    {
        PlaybackCoordinator.MarkUserChose(); PlaybackCoordinator.Claim(MusicProvider.Netease); Fm.Resume();
    }
}
