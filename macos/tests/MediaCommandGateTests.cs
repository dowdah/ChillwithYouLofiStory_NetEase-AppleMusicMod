using System;
using System.Runtime.InteropServices;
using MusicBridge;

internal static class MediaCommandGateTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    public static void Run()
    {
        Check(Marshal.SizeOf<NativeMediaSnapshot>() == 64 && Marshal.SizeOf<NativeMediaCommand>() == 64,
            "native media ABI structs have fixed 64-byte layout");
        var gate = new MediaCommandGate();
        NativeMediaCommand command = new NativeMediaCommand { Abi = 1, Size = 64,
            OwnerEpoch = 8, TrackToken = 100, Sequence = 1, Type = (int)MediaCommandKind.Next,
            AgeSeconds = 0.05 };
        Check(gate.Accept(command, 8, 100, true), "first Next accepted");
        command.Sequence = 2;
        Check(gate.Accept(command, 8, 200, true), "second Next survives first track change");
        Check(!gate.Accept(command, 8, 200, true), "same native sequence cannot execute twice");
        command.Sequence = 3; command.Type = (int)MediaCommandKind.Seek;
        Check(!gate.Accept(command, 8, 200, true), "late seek cannot target replacement track");
        command.Sequence = 4; command.TrackToken = 200; command.AgeSeconds = 2.1;
        Check(!gate.Accept(command, 8, 200, true), "aged command is rejected");
        command.Sequence = 5; command.AgeSeconds = 0.05; command.OwnerEpoch = 7;
        Check(!gate.Accept(command, 8, 200, true), "old owner command is rejected");
        command.Sequence = 6; command.OwnerEpoch = 8;
        Check(gate.Accept(command, 8, 200, true), "current seek accepted");
        gate.Reset(); command.Sequence = 1;
        Check(gate.Accept(command, 8, 200, true), "bridge retry resets sequence gate");
    }
}
