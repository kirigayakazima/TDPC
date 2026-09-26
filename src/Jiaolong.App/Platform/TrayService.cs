namespace Jiaolong.App.Platform;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public sealed class TrayService : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAY = WM_USER + 101;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        int nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        byte[] pbIconBits,
        uint cbIconBits,
        bool fIcon,
        uint dwVersion,
        int cxDesired,
        int cyDesired,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private bool _added;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _ownIcon;
    private readonly Action _onActivate;
    private readonly Action _onContextMenu;

    public TrayService(Window window, Action onActivate, Action onContextMenu)
    {
        _onActivate = onActivate;
        _onContextMenu = onContextMenu;

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("HwndSource not found");
        _source.AddHook(WndProc);

        LoadIcon();
        AddTrayIcon();
    }

    private void LoadIcon()
    {
        // 1. Try extracting small icon from current .exe PE header
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                ExtractIconEx(exe, 0, out IntPtr hLarge, out IntPtr hSmall, 1);
                if (hSmall != IntPtr.Zero)
                {
                    if (hLarge != IntPtr.Zero) DestroyIcon(hLarge);
                    _hIcon = hSmall;
                    _ownIcon = true;
                    return;
                }
                if (hLarge != IntPtr.Zero)
                {
                    _hIcon = hLarge;
                    _ownIcon = true;
                    return;
                }
            }
        }
        catch { }

        // 2. Try loading from embedded pack URI stream
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                IntPtr h = CreateIconFromBytes(bytes, 16);
                if (h != IntPtr.Zero)
                {
                    _hIcon = h;
                    _ownIcon = true;
                    return;
                }
            }
        }
        catch { }

        // 3. Fallback to standard application icon
        _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
        _ownIcon = false;
    }

    private static IntPtr CreateIconFromBytes(byte[] ico, int targetSize)
    {
        if (ico.Length < 6) return IntPtr.Zero;
        ushort type = BitConverter.ToUInt16(ico, 2);
        if (type != 1) return IntPtr.Zero;
        ushort count = BitConverter.ToUInt16(ico, 4);
        if (count == 0 || ico.Length < 6 + count * 16) return IntPtr.Zero;

        int bestIndex = -1;
        int bestDiff = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int entryOffset = 6 + i * 16;
            int width = ico[entryOffset];
            if (width == 0) width = 256;
            int diff = Math.Abs(width - targetSize);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIndex = i;
            }
        }

        if (bestIndex < 0) return IntPtr.Zero;

        int selectedEntry = 6 + bestIndex * 16;
        uint bytesInRes = BitConverter.ToUInt32(ico, selectedEntry + 8);
        uint imageOffset = BitConverter.ToUInt32(ico, selectedEntry + 12);

        if (imageOffset + bytesInRes > (uint)ico.Length) return IntPtr.Zero;

        byte[] iconData = new byte[bytesInRes];
        Buffer.BlockCopy(ico, (int)imageOffset, iconData, 0, (int)bytesInRes);

        return CreateIconFromResourceEx(
            iconData,
            (uint)iconData.Length,
            true,
            0x00030000,
            targetSize,
            targetSize,
            0);
    }

    private void AddTrayIcon()
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _hIcon,
            szTip = "TDPC · 蛟龙独立控制台"
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    public void SetTooltip(string tip)
    {
        if (!_added) return;
        if (tip.Length > 127) tip = tip.Substring(0, 127);
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_TIP,
            szTip = tip
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    public void ShowToast(string title, string message)
    {
        if (!_added) return;
        if (title.Length > 63) title = title.Substring(0, 63);
        if (message.Length > 255) message = message.Substring(0, 255);

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_INFO,
            szInfoTitle = title,
            szInfo = message,
            dwInfoFlags = 0x01 // NIIF_INFO
        };
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY)
        {
            int cmd = (int)(lParam.ToInt64() & 0xFFFF);
            if (cmd is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                _onActivate();
                handled = true;
            }
            else if (cmd == WM_RBUTTONUP)
            {
                SetForegroundWindow(_hwnd);
                _onContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _added = false;
        }

        try { _source.RemoveHook(WndProc); } catch { }
        if (_ownIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
