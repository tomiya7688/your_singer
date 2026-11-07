using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YourSinger.Process.Processing.Dataset;

// { 責務: [VerifiedFileInput: 開いたファイルの実体パスを確認し、検査とopenの間の差し替えを拒否する] }
internal static class VerifiedFileInput
{
    private const int LinuxOpenNonBlocking = 0x800;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxAtEmptyPath = 0x1000;
    private const int LinuxNoSuchFile = 2;
    private const int LinuxNotDirectory = 20;
    private const uint LinuxStatType = 1;
    private const ushort LinuxFileTypeMask = 0xf000;
    private const ushort LinuxRegularFile = 0x8000;
    private const uint WindowsDiskFile = 1;

    // { 責務: [OpenRead: Linuxでは特殊ファイルへの差し替えでopenが待機しないようにする] }
    internal static FileStream OpenRead(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("このOSでは測定入力の安全な実体確認に対応していません。");
        // { 処理: [FIFOへの差し替えで待機しないよう非ブロックで開く] }
        var descriptor = OpenLinux(path, LinuxOpenNonBlocking | LinuxOpenCloseOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == LinuxNoSuchFile) throw new FileNotFoundException("測定入力が見つかりません。", path);
            if (error == LinuxNotDirectory) throw new DirectoryNotFoundException("測定入力の親ディレクトリが見つかりません。");
            throw new IOException("測定入力を開けません。", new System.ComponentModel.Win32Exception(error));
        }
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read, 81920, false); }
        catch { handle.Dispose(); throw; }
    }

    // { 責務: [OpenLinux: 非ブロックで入力ファイル記述子を開く] }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinux(string path, int flags);

    // { 責務: [RequireRegular: 開いたハンドル自体が通常ファイルかを確認する] }
    internal static void RequireRegular(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            // { 処理: [ディスク以外のパイプやデバイスを拒否する] }
            if (GetFileType(stream.SafeFileHandle) != WindowsDiskFile)
                throw new InvalidDataException("測定入力には通常ファイルが必要です。");
            return;
        }
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("このOSでは測定入力の安全な実体確認に対応していません。");
        // { 処理: [statxで開いたファイル記述子の種別を調べる] }
        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        if (Statx(descriptor, "", LinuxAtEmptyPath, LinuxStatType, out var info) != 0 || (info.Mask & LinuxStatType) == 0)
            throw new IOException("開いた入力ファイルの種別を確認できません。");
        if ((info.Mode & LinuxFileTypeMask) != LinuxRegularFile)
            throw new InvalidDataException("測定入力には通常ファイルが必要です。");
    }

    // { 責務: [LinuxStatx: Linux statxのファイル種別を受け取る] }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    // { 責務: [Statx: ファイル記述子の種別を取得する] }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, string path, int flags, uint mask, out LinuxStatx info);

    // { 責務: [GetFileType: Windowsハンドルの種類を取得する] }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    // { 責務: [RequireExpected: ハンドルの実体が検査済みパスと一致することを確認する] }
    internal static void RequireExpected(FileStream stream, string expected)
    {
        string actual;
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("開いた入力ファイルの実体パスを確認できません。");
            actual = StripWindowsPrefix(buffer.ToString());
            expected = ExpandWindowsPath(expected);
        }
        else if (OperatingSystem.IsLinux())
        {
            var link = $"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}";
            actual = File.ResolveLinkTarget(link, true)?.FullName
                ?? throw new IOException("開いた入力ファイルの実体パスを確認できません。");
        }
        else throw new PlatformNotSupportedException("このOSでは測定入力の安全な実体確認に対応していません。");
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("測定入力が検査後に差し替えられました。");
    }

    // { 責務: [ExpandWindowsPath: 8.3短縮名をリンクを解決せず長い名前へ展開する] }
    private static string ExpandWindowsPath(string path)
    {
        var full = Path.GetFullPath(path);
        var query = full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? full :
            full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var buffer = new StringBuilder(32768);
        var length = GetLongPathName(query, buffer, (uint)buffer.Capacity);
        if (length == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException("検査対象のファイルが見つかりません。", full);
            if (error == 3) throw new DirectoryNotFoundException("検査対象の親ディレクトリが見つかりません。");
            throw new IOException("検査対象の長いパス名を確認できません。", new System.ComponentModel.Win32Exception(error));
        }
        if (length >= buffer.Capacity)
            throw new IOException("検査対象の長いパス名を確認できません。");
        return StripWindowsPrefix(buffer.ToString());
    }

    // { 責務: [StripWindowsPrefix: Windows APIの拡張パス接頭辞を通常形式に戻す] }
    private static string StripWindowsPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;

    // { 責務: [GetLongPathName: Windows短縮名を長い名前へ展開する] }
    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint pathLength);

    // { 責務: [GetFinalPathNameByHandle: Windowsハンドルの実体パスを取得する] }
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint pathLength, uint flags);
}