using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace BiomeMacro.Services;

public static class WindowLayoutService
{
    // P/Invoke
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0xC00000;
    private const int WS_THICKFRAME = 0x40000;
    private const int WS_BORDER = 0x800000;
    private const int WS_POPUP = -2147483648; // 0x80000000

    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private const uint SPI_GETWORKAREA = 0x0030;
    private const int HWND_NOTOPMOST = -2;
    private const int HWND_TOP = 0;

    static WindowLayoutService()
    {
        try { SetProcessDPIAware(); } catch { }
    }

    public static void AlignWindows(IEnumerable<int> providedPids)
    {
        // 1. Identify all Roblox instances
        var pids = new HashSet<int>(providedPids);
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta")) pids.Add(p.Id);
        foreach (var p in Process.GetProcessesByName("Windows10Universal")) pids.Add(p.Id);
        
        var validProcesses = new List<Process>();
        foreach (var pid in pids)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                    validProcesses.Add(p);
            }
            catch { }
        }
        
        int count = validProcesses.Count;
        if (count == 0) return;

        // 2. Get Work Area (exclude taskbar)
        RECT workArea = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
        
        int screenWidth = workArea.Right - workArea.Left;
        int screenHeight = workArea.Bottom - workArea.Top;
        int startX = workArea.Left;
        int startY = workArea.Top;

        // 3. Calculate Best Grid (Python logic port)
        int bestCols = (int)Math.Ceiling(Math.Sqrt(count));
        int bestRows = (int)Math.Ceiling((double)count / bestCols);
        int minWaste = (bestCols * bestRows) - count;

        for (int c = bestCols + 1; c <= count; c++)
        {
            int r = (int)Math.Ceiling((double)count / c);
            if ((double)c / r > 2.5) break; 
            
            int waste = (c * r) - count;
            if (waste < minWaste)
            {
                bestCols = c;
                bestRows = r;
                minWaste = waste;
                if (waste == 0) break;
            }
        }

        int cols = bestCols;
        int rows = bestRows;
        int windowsInLastRow = count % cols;
        if (windowsInLastRow == 0) windowsInLastRow = cols;
        int lastRowIndex = rows - 1;

        // 4. Apply changes
        int i = 0;
        foreach (var process in validProcesses.OrderBy(p => p.Id))
        {
            var hWnd = process.MainWindowHandle;
            
            // Restore if minimized
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            else ShowWindow(hWnd, SW_SHOW);
            
            // Make Borderless
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER);
            style |= WS_POPUP;
            SetWindowLong(hWnd, GWL_STYLE, style);

            // Calculate Grid Position
            int r = i / cols;
            int c = i % cols;
            bool isLastRow = (r == lastRowIndex) && (windowsInLastRow < cols);

            int actualX, actualY, actualW, actualH;

            if (isLastRow)
            {
                // Fill width for last row
                int lastRowCol = i - (lastRowIndex * cols);
                actualX = startX + (int)(lastRowCol * ((double)screenWidth / windowsInLastRow));
                actualY = startY + (int)(r * ((double)screenHeight / rows));
                actualW = (startX + (int)((lastRowCol + 1) * ((double)screenWidth / windowsInLastRow))) - actualX;
                actualH = (startY + (int)((r + 1) * ((double)screenHeight / rows))) - actualY;
            }
            else
            {
                actualX = startX + (int)(c * ((double)screenWidth / cols));
                actualY = startY + (int)(r * ((double)screenHeight / rows));
                actualW = (startX + (int)((c + 1) * ((double)screenWidth / cols))) - actualX;
                actualH = (startY + (int)((r + 1) * ((double)screenHeight / rows))) - actualY;
            }

            // Apply Position 
            // User reported: "new app is behind windows and i cant see anything else"
            // Fix: Place windows at the BOTTOM of Z-order, so new apps open on top.
            SetWindowPos(hWnd, new IntPtr(1), actualX, actualY, actualW, actualH, SWP_NOACTIVATE | SWP_FRAMECHANGED); // 1 = HWND_BOTTOM
            
            i++;
        }
    }
}
