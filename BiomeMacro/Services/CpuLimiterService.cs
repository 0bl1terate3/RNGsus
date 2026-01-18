using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.InteropServices;

namespace BiomeMacro.Services;

public class CpuLimiterService : IDisposable
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _limiterTasks = new();
    
    // P/Invokes for Thread Suspension (Undocumented API)
    [DllImport("ntdll.dll", PreserveSig = false)]
    private static extern void NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", PreserveSig = false)]
    private static extern void NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int PROCESS_SUSPEND_RESUME = 0x0800;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_READ = 0x0010;

    public void StartLimiting(int pid, int reductionPercentage)
    {
        // Stop existing limit for this PID if any
        StopLimiting(pid);

        // Calculate duty cycle (Total 100ms)
        // Reduction 20% = Sleep 20ms (Suspend), Run 80ms (Resume) -> 100ms cycle
        // Reduction 80% = Sleep 80ms, Run 20ms
        int cycleTime = 100; // ms
        int sleepTime = (int)(cycleTime * (reductionPercentage / 100.0));
        int runTime = cycleTime - sleepTime;

        // Safety clamp: Don't suspend for > 95ms (keep network heartbeat alive)
        if (sleepTime > 95) sleepTime = 95;
        if (runTime < 5) runTime = 5;

        var cts = new CancellationTokenSource();
        _limiterTasks[pid] = cts;

        var token = cts.Token;
        
        new Thread(() =>
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (handle == IntPtr.Zero) return;

                while (!token.IsCancellationRequested)
                {
                    try 
                    {
                        NtSuspendProcess(handle);
                        Thread.Sleep(sleepTime);
                        
                        NtResumeProcess(handle);
                        Thread.Sleep(runTime);
                    }
                    catch
                    {
                        // ensure we resume if we crash mid-loop
                        NtResumeProcess(handle); 
                        break;
                    }
                }
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    // Always resume before closing
                    NtResumeProcess(handle);
                    CloseHandle(handle);
                }
            }
        })
        {
            IsBackground = true,
            Name = $"CpuLimiter_{pid}"
        }.Start();
    }

    public void StartAutoLimiting(int pid)
    {
        StopLimiting(pid);

        var cts = new CancellationTokenSource();
        _limiterTasks[pid] = cts;
        var token = cts.Token;

        new Thread(() =>
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                // Request Query Info access to read CPU times
                handle = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                if (handle == IntPtr.Zero) return;

                using var process = System.Diagnostics.Process.GetProcessById(pid);
                
                // Initial parameters
                // Initial parameters
                int cycleTime = 400; // ms (Increased cycle for better <1% resolution)
                int reduction = 95; // Start very high
                
                DateTime lastCheck = DateTime.Now;
                TimeSpan lastProcessorTime = process.TotalProcessorTime;
                
                while (!token.IsCancellationRequested)
                {
                    // 1. Calculate Sleep/Run times based on current reduction
                    int sleepTime = (int)(cycleTime * (reduction / 100.0));
                    int runTime = cycleTime - sleepTime;
                    
                    // Clamps (Ensure at least 5ms run time)
                    if (runTime < 5) 
                    {
                        runTime = 5;
                        sleepTime = cycleTime - 5;
                    }

                    try
                    {
                        // 2. Execute Duty Cycle
                        NtSuspendProcess(handle);
                        Thread.Sleep(sleepTime);
                        NtResumeProcess(handle);
                        Thread.Sleep(runTime);

                        // 3. Feedback Loop (Check CPU Usage every 1s)
                        if ((DateTime.Now - lastCheck).TotalMilliseconds > 1000)
                        {
                            process.Refresh(); // Important to get new times
                            var currentProcessorTime = process.TotalProcessorTime;
                            var timeSinceLastCheck = DateTime.Now - lastCheck;
                            
                            // Calculate CPU Usage % (approximate)
                            double cpuUsage = (currentProcessorTime - lastProcessorTime).TotalMilliseconds / timeSinceLastCheck.TotalMilliseconds * 100.0 / Environment.ProcessorCount;
                            
                            // Target: < 1.0%
                            if (cpuUsage > 1.0)
                            {
                                // Too high, increase reduction (more throttle)
                                reduction = Math.Min(99, reduction + 5);
                            }
                            else if (cpuUsage < 0.1) 
                            {
                                // Too low (risk of disconnect), decrease reduction
                                reduction = Math.Max(50, reduction - 2);
                            }

                            lastCheck = DateTime.Now;
                            lastProcessorTime = currentProcessorTime;
                        }
                    }
                    catch
                    {
                        NtResumeProcess(handle);
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    NtResumeProcess(handle);
                    CloseHandle(handle);
                }
            }
        })
        {
            IsBackground = true,
            Name = $"AutoCpuLimiter_{pid}"
        }.Start();
    }

    public void StopLimiting(int pid)
    {
        if (_limiterTasks.TryRemove(pid, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void StopAll()
    {
        foreach (var pid in _limiterTasks.Keys)
        {
            StopLimiting(pid);
        }
    }

    public void Dispose()
    {
        StopAll();
    }
}
