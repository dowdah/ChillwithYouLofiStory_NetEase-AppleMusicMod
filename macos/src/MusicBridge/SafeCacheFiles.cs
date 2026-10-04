using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicBridge;

// Directory-relative unlink keeps external symlink swaps from redirecting deletion
// outside BridgePaths.Root. IoGate still serializes this Mod's own writers/leases.
internal static class SafeCacheFiles
{
    private const int O_RDONLY = 0;
    private const int O_NOFOLLOW = 0x100;
    private const int O_DIRECTORY = 0x100000;
    private const int O_CLOEXEC = 0x1000000;
    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, byte[] name, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, byte[] name, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "close")]
    private static extern int Close(int handle);

    private static byte[] Utf8(string text)
    {
        byte[] value = Encoding.UTF8.GetBytes(text);
        byte[] nul = new byte[value.Length + 1];
        Buffer.BlockCopy(value, 0, nul, 0, value.Length);
        return nul;
    }

    internal static bool DeleteRegular(string candidate)
    {
        string path = BridgePaths.ValidateWritePath(candidate);
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("缓存目标是符号链接，拒绝删除");
        string root = BridgePaths.Root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        string[] parts = path.Substring(root.Length + 1).Split(System.IO.Path.DirectorySeparatorChar);
        if (parts.Length < 2 || Array.Exists(parts, p => p.Length == 0 || p == "." || p == ".."))
            throw new IOException("缓存删除路径无效");
        int current = Open(Utf8(root), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (current < 0) throw new IOException("无法打开缓存根目录");
        try
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                int next = OpenAt(current, Utf8(parts[i]), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
                if (next < 0) throw new IOException("缓存目录在删除前发生变化");
                Close(current); current = next;
            }
            int file = OpenAt(current, Utf8(parts[parts.Length - 1]), O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
            if (file < 0) throw new IOException("缓存文件在删除前发生变化");
            Close(file);
            if (UnlinkAt(current, Utf8(parts[parts.Length - 1]), 0) != 0)
                throw new IOException("缓存文件删除失败，errno=" + Marshal.GetLastWin32Error());
            return true;
        }
        finally { Close(current); }
    }
}
