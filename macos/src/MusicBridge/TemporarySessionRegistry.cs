using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicBridge;

// Each new work file carries a session ID backed by a process-held advisory lock.
// Old unmarked .part files are intentionally not eligible for automatic cleanup.
internal static class TemporarySessionRegistry
{
    private const int O_RDWR = 2, O_CREAT = 0x200, O_EXCL = 0x800;
    private const int O_NOFOLLOW = 0x100, O_CLOEXEC = 0x1000000;
    private const int LOCK_EX = 2, LOCK_NB = 4;
    private static readonly object Gate = new object();
    private static int _handle = -1;
    private static string _id, _lockPath;

    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags, int mode);
    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int handle, int operation);
    [DllImport("libSystem.B.dylib", EntryPoint = "close")]
    internal static extern int Close(int handle);

    private static byte[] Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] nul = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, nul, 0, bytes.Length);
        return nul;
    }

    private static string LockPath(string work, string id) =>
        BridgePaths.ValidateWritePath(System.IO.Path.Combine(work, "session-" + id + ".lock"));

    internal static string NewFile(string work)
    {
        lock (Gate)
        {
            if (_handle < 0 || !File.Exists(_lockPath))
            {
                if (_handle >= 0) Close(_handle);
                _id = Guid.NewGuid().ToString("N");
                _lockPath = LockPath(work, _id);
                _handle = Open(Utf8(_lockPath), O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 384);
                if (_handle < 0 || Flock(_handle, LOCK_EX | LOCK_NB) != 0)
                {
                    if (_handle >= 0) Close(_handle);
                    _handle = -1;
                    throw new IOException("无法登记活动临时文件会话");
                }
            }
            return BridgePaths.ValidateWritePath(System.IO.Path.Combine(work,
                _id + "-" + Guid.NewGuid().ToString("N") + ".part"));
        }
    }

    internal static bool TryAcquireInactive(string work, string id, out int handle)
    {
        handle = -1;
        if (!Guid.TryParseExact(id, "N", out _)) return false;
        lock (Gate) if (_handle >= 0 && id == _id) return false;
        string path;
        try { path = LockPath(work, id); }
        catch { return false; }
        if (!File.Exists(path)) return false;
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false; }
        catch { return false; }
        int fd = Open(Utf8(path), O_RDWR | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0) return false;
        if (Flock(fd, LOCK_EX | LOCK_NB) != 0) { Close(fd); return false; }
        handle = fd;
        return true;
    }
}
