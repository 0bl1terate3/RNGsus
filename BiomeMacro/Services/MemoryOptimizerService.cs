using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BiomeMacro.Services;

public class MemoryOptimizerService : IDisposable
{
    public event EventHandler? OnThresholdReached;
    
    private CancellationTokenSource? _cts;
    private int _thresholdPercentage = 80;
    private bool _isRunning;

    // P/Invoke for Memory Status
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        
        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public void UpdateThreshold(int percentage)
    {
        _thresholdPercentage = Math.Clamp(percentage, 40, 99);
    }

    public void StartMonitoring()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        
        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (CheckMemoryUsage())
                {
                    OnThresholdReached?.Invoke(this, EventArgs.Empty);
                    // Cooldown of 30s to prevent spamming clean if memory stays high
                    await Task.Delay(30000, _cts.Token); 
                }
                
                await Task.Delay(5000, _cts.Token); // Check every 5s
            }
        }, _cts.Token);
    }

    public void StopMonitoring()
    {
        _isRunning = false;
        _cts?.Cancel();
    }

    private bool CheckMemoryUsage()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                return memStatus.dwMemoryLoad >= _thresholdPercentage;
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        StopMonitoring();
        _cts?.Dispose();
    }
}
