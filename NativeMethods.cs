using System.Runtime.InteropServices;
using System.Text;

namespace AppLockerOverlay;

[StructLayout(LayoutKind.Sequential)]
internal struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;
}

internal static class NativeMethods
{
    public const int GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    public const int SM_CYCAPTION = 4;

    public const int SM_CYFRAME = 33;

    public const uint SWP_NOSIZE = 0x0001;

    public const uint SWP_NOMOVE = 0x0002;

    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public const int SW_HIDE = 0;

    public const int SW_SHOWNORMAL = 1;

    public const int SW_SHOWMINIMIZED = 2;

    public const int SW_SHOWMAXIMIZED = 3;

    public const int SW_SHOW = 5;

    public const int SW_MINIMIZE = 6;

    public const int SW_RESTORE = 9;

    public const uint SWP_FRAMECHANGED = 0x0020;

    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint SWP_NOZORDER = 0x0004;

    public const int WM_APPCOMMAND = 0x319;

    /// <summary>Stop the focused/target media session (lParam = command &lt;&lt; 16).</summary>
    public const int AppCommandMediaStop = 13;

    /// <summary>Toggle play/pause for the focused/target media session.</summary>
    public const int AppCommandMediaPlayPause = 14;

    /// <summary>Pause the focused/target media session.</summary>
    public const int AppCommandMediaPause = 47;

    public const byte VkMediaStop = 0xB2;

    public const byte VkMediaPlayPause = 0xB3;

    public const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    public const uint WM_CLOSE = 0x0010;

    public static bool TryGetWindowPlacement(nint hWnd, out WINDOWPLACEMENT placement)
    {
        placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>()
        };
        if (!IsWindow(hWnd) || !GetWindowPlacement(hWnd, ref placement))
        {
            return false;
        }

        // While maximized, some hosts still report SHOWNORMAL — trust IsZoomed.
        if (IsZoomed(hWnd))
        {
            placement.showCmd = SW_SHOWMAXIMIZED;
        }

        return true;
    }

    public static void TryRestoreWindowPlacement(nint hWnd, WINDOWPLACEMENT placement)
    {
        if (!IsWindow(hWnd))
        {
            return;
        }

        placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        // Never re-show as hidden after unlock.
        if (placement.showCmd is SW_HIDE or 0)
        {
            placement.showCmd = SW_SHOWNORMAL;
        }

        _ = SetWindowPlacement(hWnd, ref placement);
        _ = SetWindowPos(
            hWnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public static void SendMediaAppCommand(nint hWnd, int appCommand)
    {
        if (hWnd == nint.Zero || !IsWindow(hWnd))
        {
            return;
        }

        _ = SendMessage(hWnd, WM_APPCOMMAND, hWnd, (nint)(appCommand << 16));
    }

    public static void TapMediaKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, 0);
        keybd_event(virtualKey, 0, KeyeventfKeyup, 0);
    }

    /// <summary>Places a window above other non-topmost windows when passed to <see cref="SetWindowPos"/>.</summary>
    public static readonly nint HwndTop = nint.Zero;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>Best-effort hint so Windows may restart this executable after certain failures (does not stop hard termination).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

    public const int RestartNoCrash = 0x1;

    public const int RestartNoHang = 0x2;

    /// <summary>Removes restart metadata registered with <see cref="RegisterApplicationRestart"/>.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int UnregisterApplicationRestart();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Approximate seconds since last keyboard or mouse input anywhere in this session.</summary>
    public static int GetLastInputIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii))
        {
            return 0;
        }

        var now = (uint)Environment.TickCount;
        var idleMs = now - lii.dwTime;
        return (int)(idleMs / 1000u);
    }

    public const uint CreateNoWindow = 0x08000000;
    public const uint DetachedProcess = 0x00000008;
    public const uint CreateNewProcessGroup = 0x00000200;
    public const uint CreateBreakawayFromJob = 0x01000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    /// <summary>
    /// Starts <paramref name="exePath"/> detached from this process tree so Task Manager "End task" on the hub
    /// does not also kill the watchdog helper.
    /// </summary>
    public static bool TryStartDetachedProcess(string exePath, string arguments, string? workingDirectory, out int processId)
    {
        processId = 0;
        // CreateProcessW may rewrite the command line buffer — it must not be a literal/readonly string.
        var commandLine = new StringBuilder();
        commandLine.Append('"').Append(exePath).Append('"');
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            commandLine.Append(' ').Append(arguments);
        }

        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>()
        };

        var flags = DetachedProcess | CreateNewProcessGroup | CreateNoWindow | CreateBreakawayFromJob;
        if (!CreateProcess(
                exePath,
                commandLine,
                nint.Zero,
                nint.Zero,
                false,
                flags,
                nint.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startup,
                out var info))
        {
            // Some hosts reject BREAKAWAY; retry without it.
            commandLine.Clear();
            commandLine.Append('"').Append(exePath).Append('"');
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                commandLine.Append(' ').Append(arguments);
            }

            flags = DetachedProcess | CreateNewProcessGroup | CreateNoWindow;
            if (!CreateProcess(
                    exePath,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    flags,
                    nint.Zero,
                    string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                    ref startup,
                    out info))
            {
                return false;
            }
        }

        processId = (int)info.dwProcessId;
        if (info.hThread != nint.Zero)
        {
            _ = CloseHandle(info.hThread);
        }

        if (info.hProcess != nint.Zero)
        {
            _ = CloseHandle(info.hProcess);
        }

        return processId > 0;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFO
{
    public int cb;
    public string? lpReserved;
    public string? lpDesktop;
    public string? lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public nint lpReserved2;
    public nint hStdInput;
    public nint hStdOutput;
    public nint hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public nint hProcess;
    public nint hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}
