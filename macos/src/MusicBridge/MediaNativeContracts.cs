using System.Runtime.InteropServices;

namespace MusicBridge;

internal enum MediaCommandKind { Play = 1, Pause = 2, Toggle = 3, Next = 4, Previous = 5, Seek = 6 }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMediaSnapshot
{
    public uint Abi, Size;
    public ulong OwnerEpoch, TrackToken;
    public int State, Capabilities;
    public double PositionSeconds, DurationSeconds;
    public System.IntPtr Title, Artist;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMediaCommand
{
    public uint Abi, Size;
    public ulong Sequence, OwnerEpoch, TrackToken;
    public int Type, Reserved;
    public double SeekSeconds, ReceivedMonotonicSeconds, AgeSeconds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMediaDiagnostics
{
    public uint Abi, Size;
    public ulong Received, Accepted, Rejected;
    public uint QueueDepth, RegisteredTargets;
}
