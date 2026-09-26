using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicBridge;

internal sealed class MacSystemMediaBridge
{
    private const string Library = "libmusicbridge_media";
    private const uint Abi = 1;
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_abi_version")]
    private static extern uint AbiVersion();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_initialize")]
    private static extern int InitializeNative();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_publish")]
    private static extern int PublishNative(ref NativeMediaSnapshot snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_deactivate")]
    private static extern void DeactivateNative(ulong epoch);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_poll")]
    private static extern int PollNative(out NativeMediaCommand command);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_registered_target_count")]
    private static extern int TargetCountNative();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mb_media_shutdown")]
    private static extern void ShutdownNative();

    public bool Loaded { get; private set; }
    public string Error { get; private set; }
    public int RegisteredTargets
    {
        get { try { return Loaded ? TargetCountNative() : 0; } catch { return 0; } }
    }

    public bool Initialize()
    {
        try
        {
            if (Marshal.SizeOf<NativeMediaSnapshot>() != 64 || Marshal.SizeOf<NativeMediaCommand>() != 64 ||
                AbiVersion() != Abi || InitializeNative() != 1)
                throw new InvalidOperationException("媒体桥接ABI或系统API不可用");
            Loaded = true; Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Loaded = false;
            Error = ex.GetType().Name + (ex is InvalidOperationException ? "：" + ex.Message : "");
            return false;
        }
    }

    private static IntPtr Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        return pointer;
    }

    public bool Publish(PlaybackSnapshot value)
    {
        if (!Loaded || string.IsNullOrEmpty(value.Title)) return false;
        var snapshot = new NativeMediaSnapshot
        {
            Abi = Abi, Size = (uint)Marshal.SizeOf<NativeMediaSnapshot>(),
            OwnerEpoch = (ulong)value.OwnerEpoch,
            TrackToken = TrackTokenOf(value.TrackKey),
            State = value.State == PlaybackState.Playing && value.DesiredPlaying ? 1 : 2,
            Capabilities = (value.CanPlay ? 1 : 0) | (value.CanPause ? 2 : 0) |
                (value.CanNext ? 4 : 0) | (value.CanPrevious ? 8 : 0) | (value.CanSeek ? 16 : 0),
            PositionSeconds = value.Position,
            DurationSeconds = value.Duration,
            Title = Utf8(value.Title), Artist = Utf8(value.Artist)
        };
        try { return PublishNative(ref snapshot) == 1; }
        catch (Exception ex) { Error = ex.GetType().Name; Loaded = false; return false; }
        finally { Marshal.FreeHGlobal(snapshot.Title); Marshal.FreeHGlobal(snapshot.Artist); }
    }

    public static ulong TrackTokenOf(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte part in Encoding.UTF8.GetBytes(value ?? "")) hash = (hash ^ part) * prime;
        return hash;
    }

    public bool Poll(out NativeMediaCommand command)
    {
        command = default;
        if (!Loaded) return false;
        try { return PollNative(out command) == 1; }
        catch (Exception ex) { Error = ex.GetType().Name; Loaded = false; return false; }
    }

    public void Deactivate(ulong epoch)
    {
        if (!Loaded) return;
        try { DeactivateNative(epoch); }
        catch (Exception ex) { Error = ex.GetType().Name; Loaded = false; }
    }

    public void Shutdown()
    {
        if (!Loaded) return;
        try { ShutdownNative(); }
        catch (Exception ex) { Error = ex.GetType().Name; }
        Loaded = false;
    }
}
