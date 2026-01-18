using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BiomeMacro.Services;

/// <summary>
/// Anti-AFK service using ViGEmBus virtual gamepad for reliable background input.
/// Uses "Focus Spoofing" to trick all windows into accepting input simultaneously.
/// </summary>
public class AntiAfkService : IDisposable
{
    private readonly MultiInstanceManager _instanceManager;
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isInitialized;
    
    public event Action<string>? OnStatus;
    public event Action<DateTime>? OnJumpSent;
    
    public bool IsRunning { get; private set; }
    public bool EnableJump { get; set; } = true;
    public bool EnableWalk { get; set; } = false;
    public bool EnableSpin { get; set; } = false;
    
    // Interval Range
    private int _minInterval = 10;
    private int _maxInterval = 60;
    
    public int MinInterval
    {
        get => _minInterval;
        set => _minInterval = Math.Clamp(value, 1, _maxInterval);
    }
    
    public int MaxInterval
    {
        get => _maxInterval;
        set => _maxInterval = Math.Max(value, _minInterval);
    }
    
    // Legacy support for fixed interval property (maps to Max)
    public int IntervalSeconds
    {
        get => _maxInterval;
        set { _maxInterval = value; _minInterval = Math.Max(1, value - 10); } // Approximate
    }
    
    // P/Invoke definitions for Focus Spoofing
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_NCACTIVATE = 0x0086;
    private const int WA_ACTIVE = 1;
    private const int WA_INACTIVE = 0;
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public AntiAfkService(MultiInstanceManager instanceManager)
    {
        _instanceManager = instanceManager;
        InitializeViGEm();
    }
    
    private void InitializeViGEm()
    {
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.Connect();
            _isInitialized = true;
            OnStatus?.Invoke("ViGEmBus controller connected");
        }
        catch (Exception ex)
        {
            _isInitialized = false;
            OnStatus?.Invoke($"ViGEmBus error: {ex.Message}. Is the driver installed?");
        }
    }
    
    public void Start()
    {
        if (IsRunning) return;
        
        if (!_isInitialized)
        {
            OnStatus?.Invoke("Cannot start: ViGEmBus driver not available");
            return;
        }
        
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _loopTask = Task.Run(AntiAfkLoop);
        OnStatus?.Invoke("Anti-AFK started");
    }
    
    public void Stop()
    {
        if (!IsRunning) return;
        
        _cts?.Cancel();
        IsRunning = false;
        OnStatus?.Invoke("Anti-AFK stopped");
    }
    
    private async Task AntiAfkLoop()
    {
        var random = new Random();
        
        while (!_cts!.Token.IsCancellationRequested)
        {
            try
            {
                // Calculate random interval
                int delaySeconds = random.Next(_minInterval, _maxInterval + 1);
                
                // Wait
                await Task.Delay(delaySeconds * 1000, _cts.Token);
                
                // Perform Actions
                await PerformActions(random);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatus?.Invoke($"Anti-AFK error: {ex.Message}");
            }
        }
    }
    
    private async Task PerformActions(Random random)
    {
        if (_controller == null || !_isInitialized) return;
        
        // Collect handles once
        var handles = GetActiveWindowHandles();
        if (handles.Count == 0) return;

        try
        {
            // Spoof Focus ON
            foreach (var h in handles) SpoofFocus(h, true);
            
            // 1. Jump
            if (EnableJump)
            {
                _controller.SetButtonState(Xbox360Button.A, true);
                _controller.SubmitReport();
                await Task.Delay(100);
                _controller.SetButtonState(Xbox360Button.A, false);
                _controller.SubmitReport();
                await Task.Delay(50);
            }
            
            // 2. Walk (Random direction)
            if (EnableWalk)
            {
                // Simulate Left Stick movement
                short x = (short)random.Next(-30000, 30000);
                short y = (short)random.Next(-30000, 30000);
                
                _controller.SetAxisValue(Xbox360Axis.LeftThumbX, x);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, y);
                _controller.SubmitReport();
                await Task.Delay(200); // Walk for 200ms
                
                _controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                _controller.SubmitReport();
                await Task.Delay(50);
            }
            
            // 3. Spin (Random direction)
            if (EnableSpin)
            {
                short spin = (short)(random.Next(0, 2) == 0 ? -20000 : 20000);
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, spin);
                _controller.SubmitReport();
                await Task.Delay(150);
                
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                _controller.SubmitReport();
            }

            // Spoof Focus OFF
            foreach (var h in handles) SpoofFocus(h, false);
            
            OnJumpSent?.Invoke(DateTime.Now);
            OnStatus?.Invoke($"Performed actions on {handles.Count} windows");
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"Action error: {ex.Message}");
        }
    }

    private List<IntPtr> GetActiveWindowHandles()
    {
        var handles = new List<IntPtr>();
        var pids = _instanceManager.Instances.Keys.ToList();
        
        foreach (var pid in pids)
        {
            try 
            {
                var proc = Process.GetProcessById(pid);
                if (proc.MainWindowHandle != IntPtr.Zero)
                    handles.Add(proc.MainWindowHandle);
            }
            catch { }
        }
        return handles;
    }
    
    private void SpoofFocus(IntPtr hwnd, bool active)
    {
        if (active)
        {
            PostMessage(hwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
            PostMessage(hwnd, WM_ACTIVATE, (IntPtr)WA_ACTIVE, IntPtr.Zero);
            PostMessage(hwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            PostMessage(hwnd, WM_ACTIVATE, (IntPtr)WA_INACTIVE, IntPtr.Zero);
            PostMessage(hwnd, WM_NCACTIVATE, (IntPtr)0, IntPtr.Zero);
        }
    }
    
    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        
        try
        {
            _controller?.Disconnect();
            _client?.Dispose();
        }
        catch { }
    }
}
