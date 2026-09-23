using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MusicBridge;

internal sealed class NativeFlacDecoder : INeteaseAudioDecoder
{
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlopen(string path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern IntPtr dlsym(IntPtr library, string symbol);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Abi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Info(IntPtr h, out int rate, out int channels, out int bits, out ulong frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ReadPcm(IntPtr h, ulong frames, [Out] float[] samples);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SeekPcm(IntPtr h, ulong frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(IntPtr h);
    private static readonly object Gate = new object();
    private static IntPtr _library; // Deliberately process-lifetime; delegates must remain callable until all sessions close.
    private static Open _open; private static Info _info; private static ReadPcm _read;
    private static SeekPcm _seek; private static Close _close;
    private static int _handles;
    public static int ActiveHandles => Volatile.Read(ref _handles);
    private IntPtr _handle;
    public PcmFormat Format { get; }
    private static T Symbol<T>(string name) where T : class
    {
        IntPtr ptr = dlsym(_library, name);
        if (ptr == IntPtr.Zero) throw new InvalidDataException("FLAC解码库接口缺失");
        return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
    }
    private static void Initialize()
    {
        lock (Gate)
        {
            if (_open != null) return;
            string path = Path.Combine(BridgePaths.Root, "libmusicbridge_flac.dylib");
            if (_library == IntPtr.Zero) _library = dlopen(path, 2); // RTLD_NOW, absolute path, no working-directory lookup.
            if (_library == IntPtr.Zero) throw new DllNotFoundException("FLAC解码库无法加载");
            if (Symbol<Abi>("mb_flac_abi")() != 1) throw new InvalidDataException("FLAC解码库版本不匹配");
            _info = Symbol<Info>("mb_flac_info"); _read = Symbol<ReadPcm>("mb_flac_read");
            _seek = Symbol<SeekPcm>("mb_flac_seek"); _close = Symbol<Close>("mb_flac_close");
            _open = Symbol<Open>("mb_flac_open");
        }
    }
    public NativeFlacDecoder(string path)
    {
        Initialize();
        _handle = _open(path);
        if (_handle == IntPtr.Zero) throw new InvalidDataException("FLAC文件损坏或规格不受支持");
        Interlocked.Increment(ref _handles);
        try
        {
            if (_info(_handle, out int rate, out int channels, out int bits, out ulong frames) != 1) throw new InvalidDataException();
            Format = new PcmFormat { SampleRate = rate, Channels = channels, BitsPerSample = bits, Frames = checked((long)frames) };
        }
        catch { Dispose(); throw; }
    }
    public int Read(float[] samples, int frames)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeFlacDecoder));
        if (frames < 0 || frames > samples.Length / Format.Channels) throw new ArgumentOutOfRangeException(nameof(frames));
        return checked((int)_read(_handle, (ulong)frames, samples));
    }
    public void Seek(long frame)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeFlacDecoder));
        if (frame < 0 || frame >= Format.Frames || _seek(_handle, (ulong)frame) == 0) throw new InvalidDataException("FLAC定位失败");
    }
    public void Dispose()
    {
        var h = _handle; _handle = IntPtr.Zero;
        if (h != IntPtr.Zero) { _close(h); Interlocked.Decrement(ref _handles); }
    }

    // Full integrity pass before cache publication. CRC-skipped/truncated frames fail the
    // frame count; STREAMINFO PCM MD5 also catches content corruption with a valid header.
    public static PcmFormat Validate(string path, Func<bool> cancelled)
    {
        byte[] header = new byte[42];
        using (var file = File.OpenRead(path))
        {
            if (file.Read(header, 0, header.Length) != header.Length || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C' || (header[4] & 127) != 0 || header[5] != 0 || header[6] != 0 || header[7] != 34)
                throw new InvalidDataException("FLAC文件头不完整");
        }
        using var decoder = new NativeFlacDecoder(path);
        using var md5 = MD5.Create();
        var format = decoder.Format;
        var pcm = new float[8192 * format.Channels];
        int width = format.BitsPerSample / 8;
        var bytes = new byte[pcm.Length * width];
        long total = 0;
        while (true)
        {
            if (cancelled()) throw new OperationCanceledException();
            int n = decoder.Read(pcm, 8192); if (n == 0) break;
            total += n;
            if (total > format.Frames) throw new InvalidDataException("FLAC帧数异常");
            for (int i = 0; i < n * format.Channels; i++)
            {
                int sample = (int)(pcm[i] * (format.BitsPerSample == 16 ? 32768.0 : 8388608.0));
                for (int b = 0; b < width; b++) bytes[i * width + b] = (byte)(sample >> (b * 8));
            }
            md5.TransformBlock(bytes, 0, n * format.Channels * width, bytes, 0);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (total != format.Frames) throw new InvalidDataException("FLAC文件截断或帧校验失败");
        bool hasDigest = false; for (int i = 26; i < 42; i++) hasDigest |= header[i] != 0;
        if (hasDigest) for (int i = 0; i < 16; i++) if (header[26 + i] != md5.Hash[i]) throw new InvalidDataException("FLAC PCM摘要校验失败");
        return format;
    }
}
