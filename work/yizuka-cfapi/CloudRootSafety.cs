using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class CloudRootSafety
{
    private const uint CloudTagMask = 0xFFFF0FFF;
    private const uint CloudTag = 0x9000001A;

    public static string? UnsafeReparseAncestor(string path)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            var tag = ReparseTag(directory.FullName);
            if (tag == 0) continue;
            if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                (tag & CloudTagMask) == CloudTag) continue;
            return directory.FullName;
        }
        return null;
    }

    private static uint ReparseTag(string path)
    {
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(16 * 1024);
        try
        {
            if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, 16 * 1024, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 4390) return 0; // ERROR_NOT_A_REPARSE_POINT
                throw new Win32Exception(error);
            }
            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
