using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FetchPkg.Core;

internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    public static bool TryEnable(SafeFileHandle handle, out string? error)
    {
        error = null;
        // Unix creates holes when SetLength/ftruncate extends a regular file.
        // There is no Windows-style sparse flag to set. Allocation still depends
        // on the volume (e.g. APFS/ext4 support holes, FAT volumes may not).
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            error = "Sparse files are not supported on this operating system.";
            return false;
        }

        if (DeviceIoControl(
                handle,
                FsctlSetSparse,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            return true;
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
