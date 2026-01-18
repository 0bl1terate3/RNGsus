using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BiomeMacro.Services;

public class InputService
{
    // Win32 API
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_NCACTIVATE = 0x0086;
    private const int WA_ACTIVE = 1;
    private const int MK_LBUTTON = 0x0001;
    
    // Virtual Keys
    private const int VK_RETURN = 0x0D;

    public void SpoofFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        PostMessage(hwnd, WM_ACTIVATE, (IntPtr)WA_ACTIVE, IntPtr.Zero);
        PostMessage(hwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
    }

    public async Task SendKey(int pid, int vkKey, bool useScanCode = true)
    {
        var mainWnd = GetWindowHandle(pid);
        if (mainWnd == IntPtr.Zero) return;

        var childWnd = FindWindowEx(mainWnd, IntPtr.Zero, "RenderWindow", null);
        var targetWnd = childWnd != IntPtr.Zero ? childWnd : mainWnd;

        uint scanCode = useScanCode ? (uint)MapVirtualKey((uint)vkKey, 0) : 0;
        IntPtr lParamDown = (IntPtr)(1 | (scanCode << 16));
        IntPtr lParamUp = (IntPtr)(1 | (scanCode << 16) | (1U << 30) | (1U << 31));

        SpoofFocus(targetWnd);
        PostMessage(targetWnd, WM_KEYDOWN, (IntPtr)vkKey, lParamDown);
        await Task.Delay(50);
        PostMessage(targetWnd, WM_KEYUP, (IntPtr)vkKey, lParamUp);
    }

    public async Task SendChar(int pid, char c)
    {
        var mainWnd = GetWindowHandle(pid);
        if (mainWnd == IntPtr.Zero) return;

        var childWnd = FindWindowEx(mainWnd, IntPtr.Zero, "RenderWindow", null);
        var targetWnd = childWnd != IntPtr.Zero ? childWnd : mainWnd;

        SpoofFocus(targetWnd);
        PostMessage(targetWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
        await Task.Delay(50);
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public async Task SendMouseClick(int pid, int x, int y, bool rightClick = false)
    {
        var mainWnd = GetWindowHandle(pid);
        if (mainWnd == IntPtr.Zero) return;

        IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        SpoofFocus(mainWnd);
        PostMessage(mainWnd, WM_MOUSEMOVE, (IntPtr)0, lParam);
        PostMessage(mainWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
        PostMessage(mainWnd, WM_LBUTTONUP, (IntPtr)0, lParam);
        await Task.Delay(10); 
    }

    public async Task SendChatCommand(int pid, string command)
    {
        var hWnd = GetWindowHandle(pid);
        if (hWnd == IntPtr.Zero) return;

        PostMessage(hWnd, WM_CHAR, (IntPtr)'/', IntPtr.Zero);
        await Task.Delay(50);

        foreach (char c in command)
        {
            PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            await Task.Delay(10);
        }

        await Task.Delay(50);
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
        await Task.Delay(50);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
    }

    public void BringToFront(int pid)
    {
        var hWnd = GetWindowHandle(pid);
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }

    public IntPtr GetWindowHandle(int pid)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid)
            {
                if (IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();
                    if (title.Contains("Roblox") || title == "Roblox")
                    {
                        result = hWnd;
                        return false; // Found it
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        // Fallback
        if (result == IntPtr.Zero)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                result = proc.MainWindowHandle;
            }
            catch { }
        }

        return result;
    }
}
