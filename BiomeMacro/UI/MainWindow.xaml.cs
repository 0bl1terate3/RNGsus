using System;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using BiomeMacro.Models;
using BiomeMacro.Services;
using BiomeMacro.UI.ViewModels;


// Aliases to resolve ambiguity with System.Drawing (from UseWindowsForms)
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Matrix = System.Windows.Media.Matrix;
using Pen = System.Windows.Media.Pen;

namespace BiomeMacro.UI;

public partial class MainWindow : Window
{
    private MultiInstanceManager? _instanceManager;
    private DiscordWebhook? _discordWebhook;
    private RobloxAvatarService? _avatarService;
    private AntiAfkService? _antiAfkService;
    private InputService? _inputService;
    private CpuLimiterService? _cpuLimiter;
    private MemoryOptimizerService? _memoryOptimizer;
    private StatisticsService? _statisticsService;
    private GraphsViewModel? _graphsViewModel;


    private bool _isMonitoring;
    private bool _isEfficiencyMode;

    private int _raresFound;
    private int _biomesSeenTotal;
    private bool _isDynamicThemeEnabled;
    private BiomeType _currentThemeBiome = BiomeType.Normal;

    // Window Management P/Invokes
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private bool _isGhostMode = false;

    // Ghost Mode Handle Cache: PID -> hWnd
    private Dictionary<int, IntPtr> _ghostHandles = new();

    // Window Search P/Invokes
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Optimization P/Invokes
    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint IDLE_PRIORITY_CLASS = 0x00000040;

    // Flash Window P/Invokes
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    public ObservableCollection<InstanceViewModel> Instances { get; } = new();
    public ObservableCollection<BiomeHistoryItem> BiomeHistory { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // Ensure UI starts with the light theme applied
        ApplyLightTheme();

        // Bind collections to all instance lists
        InstanceList.ItemsSource = Instances;
        InstanceListFull.ItemsSource = Instances;
        CpuLimiterList.ItemsSource = Instances;
        HistoryList.ItemsSource = BiomeHistory;

        InitializeServices();
        StartLiveAnimation();

        UpdateStatus("Ready to track biomes");
        UpdateStats();
        UpdateHeroBiome("Normal", BiomeType.Normal);

        // Load saved settings (future: persist to file)
        AntiAfkStatusText.Text = "INACTIVE";

        // Check ViGEmBus installation status
        CheckVigemBusInstalled();
    }

    #region ViGEmBus Driver

    private void CheckVigemBusInstalled()
    {
        try
        {
            // Check if ViGEmBus driver is installed by looking for the device
            bool isInstalled = false;

            // Method 1: Check for ViGEmBus service
            using (var sc = new System.ServiceProcess.ServiceController("ViGEmBus"))
            {
                try
                {
                    var status = sc.Status;
                    isInstalled = true;
                }
                catch { /* Service not found */ }
            }

            // Method 2: Check registry as fallback
            if (!isInstalled)
            {
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
                if (key != null)
                {
                    isInstalled = true;
                    key.Close();
                }
            }

            Dispatcher.Invoke(() =>
            {
                if (isInstalled)
                {
                    VigemStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                    VigemStatusText.Text = "INSTALLED";
                    VigemBtnText.Text = "Reinstall ViGEmBus";
                }
                else
                {
                    VigemStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
                    VigemStatusText.Text = "NOT INSTALLED";
                    VigemBtnText.Text = "Install ViGEmBus";
                }
            });
        }
        catch (Exception ex)
        {
            UpdateStatus($"ViGEmBus check failed: {ex.Message}");
        }
    }

    private async void InstallVigem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VigemBtnText.Text = "Downloading...";
            InstallVigemBtn.IsEnabled = false;

            // ViGEmBus latest release URL
            const string vigemUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ViGEmBus_Setup.exe");

            using (var client = new System.Net.Http.HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BiomeMacro/1.0");
                var response = await client.GetAsync(vigemUrl);
                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync();
                await System.IO.File.WriteAllBytesAsync(tempPath, bytes);
            }

            VigemBtnText.Text = "Installing...";

            // Run the installer
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tempPath,
                    UseShellExecute = true,
                    Verb = "runas" // Request admin elevation
                }
            };
            process.Start();
            await Task.Run(() => process.WaitForExit());

            // Recheck installation status
            CheckVigemBusInstalled();
            UpdateStatus("ViGEmBus installation complete! Focus-free Anti-AFK is now available.");
        }
        catch (Exception ex)
        {
            UpdateStatus($"ViGEmBus install failed: {ex.Message}");
            VigemBtnText.Text = "Install ViGEmBus";
        }
        finally
        {
            InstallVigemBtn.IsEnabled = true;
        }
    }

    #endregion

    #region Navigation

    private void NavDashboard_Click(object sender, RoutedEventArgs e) => SwitchPage("Dashboard");
    private void NavInstances_Click(object sender, RoutedEventArgs e) => SwitchPage("Instances");
    private void NavAntiAfk_Click(object sender, RoutedEventArgs e) => SwitchPage("AntiAfk");
    private void NavHistory_Click(object sender, RoutedEventArgs e) => SwitchPage("History");
    private void NavGraphs_Click(object sender, RoutedEventArgs e) => SwitchPage("Graphs");
    private void NavHowToUse_Click(object sender, RoutedEventArgs e) => SwitchPage("HowToUse");
    private void NavSettings_Click(object sender, RoutedEventArgs e) => SwitchPage("Settings");

    private void SwitchPage(string pageName)
    {
        // Hide all pages
        DashboardPage.Visibility = Visibility.Collapsed;
        InstancesPage.Visibility = Visibility.Collapsed;
        AntiAfkPage.Visibility = Visibility.Collapsed;
        OptimizationsPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        GraphsPage.Visibility = Visibility.Collapsed;
        HowToUsePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        // Reset all indicators
        NavDashboardIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavInstancesIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavAntiAfkIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavOptimizationsIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavHistoryIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavGraphsIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavHowToUseIndicator.Background = new SolidColorBrush(Colors.Transparent);
        NavSettingsIndicator.Background = new SolidColorBrush(Colors.Transparent);

        var accentColor = (Color)ColorConverter.ConvertFromString("#4FC3F7");
        FrameworkElement? targetPage = null;

        // Show selected page and highlight indicator
        switch (pageName)
        {
            case "Dashboard":
                targetPage = DashboardPage;
                NavDashboardIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Dashboard";
                break;
            case "Instances":
                targetPage = InstancesPage;
                NavInstancesIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Instances";
                break;
            case "History":
                targetPage = HistoryPage;
                NavHistoryIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "History";
                break;
            case "Graphs":
                targetPage = GraphsPage;
                NavGraphsIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Biome Graphs";
                break;
            case "AntiAfk":
                targetPage = AntiAfkPage;
                NavAntiAfkIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Anti-AFK";
                break;
            case "Optimizations":
                targetPage = OptimizationsPage;
                NavOptimizationsIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Optimizations";
                break;
            case "Settings":
                targetPage = SettingsPage;
                NavSettingsIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "Settings";
                break;
            case "HowToUse":
                targetPage = HowToUsePage;
                NavHowToUseIndicator.Background = new SolidColorBrush(accentColor);
                PageTitle.Text = "How To Use";
                break;
        }

        // Animate the page in
        if (targetPage != null)
        {
            targetPage.Opacity = 0;
            targetPage.RenderTransform = new TranslateTransform(0, 15);
            targetPage.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var slideUp = new DoubleAnimation(15, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            targetPage.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)targetPage.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    #endregion

    private void StartLiveAnimation()
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.3,
            Duration = TimeSpan.FromSeconds(1),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, LiveIndicator);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Opacity"));
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void InitializeServices()
    {
        _avatarService = new RobloxAvatarService();

        _discordWebhook = new DiscordWebhook();
        _discordWebhook.OnSuccess += msg => Dispatcher.Invoke(() => UpdateStatus(msg));
        _discordWebhook.OnError += msg => Dispatcher.Invoke(() => UpdateStatus($"Error: {msg}"));

        _instanceManager = new MultiInstanceManager();
        _instanceManager.OnStatus += msg => Dispatcher.Invoke(() => UpdateStatus(msg));
        _instanceManager.OnError += msg => Dispatcher.Invoke(() => UpdateStatus($"Error: {msg}"));
        _instanceManager.OnInstanceAdded += inst => Dispatcher.Invoke(() => OnInstanceAdded(inst));
        _instanceManager.OnInstanceRemoved += inst => Dispatcher.Invoke(() => OnInstanceRemoved(inst));
        _instanceManager.OnBiomeChanged += inst => Dispatcher.Invoke(() => OnBiomeChanged(inst));
        _instanceManager.OnAuraChanged += inst => Dispatcher.Invoke(() => OnAuraChanged(inst));
        _instanceManager.OnUsernameDetected += inst => Dispatcher.Invoke(() => OnUsernameDetected(inst));
        _instanceManager.OnMerchantDetected += inst => Dispatcher.Invoke(() => OnMerchantDetected(inst));
        _instanceManager.OnMerchantDetected += inst => Dispatcher.Invoke(() => OnMerchantDetected(inst));
        _instanceManager.OnJesterDetected += inst => Dispatcher.Invoke(() => OnJesterDetected(inst));
        _instanceManager.OnEdenDetected += inst => Dispatcher.Invoke(() => OnEdenDetected(inst));

        try
        {
            _inputService = new InputService();

            _antiAfkService = new AntiAfkService(_instanceManager);
            _antiAfkService.OnStatus += msg => Dispatcher.Invoke(() => UpdateStatus(msg));
            _antiAfkService.OnJumpSent += time => Dispatcher.Invoke(() =>
            {
                AntiAfkLastActionText.Text = $"Last action: {time:HH:mm:ss}";
            });


            _cpuLimiter = new CpuLimiterService();

            _statisticsService = new StatisticsService(_instanceManager);
            _graphsViewModel = new GraphsViewModel(_statisticsService);
            GraphsPage.DataContext = _graphsViewModel;

            _memoryOptimizer = new MemoryOptimizerService();
            _memoryOptimizer.OnThresholdReached += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    OptimizeMemory_Click(null!, null!);
                    UpdateStatus($"Auto Memory Cleaner: Triggered (> {MemoryThresholdSlider.Value}%)");
                });
            };


        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: Anti-AFK Failed: {ex.Message}");
            AntiAfkStatusText.Text = "ERROR";
            AntiAfkStatusBadge.Background = new SolidColorBrush(Colors.Red);
        }
    }

    private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
    {
        // Log and give immediate feedback so we can see the handler is running
        bool isDark = ThemeToggle.IsChecked == true;
        UpdateStatus($"Theme toggle clicked: {(isDark ? "Hell" : "Light")} mode");
        try
        {
            if (isDark) ApplyHellTheme();
            else ApplyLightTheme();

            // Update toggle icon for clarity
            ThemeToggle.Content = isDark ? "🌙" : "☀️";
        }
        catch (Exception ex)
        {
            UpdateStatus($"Theme toggle error: {ex.Message}");
            throw;
        }
    }

    private void ApplyHellTheme()
    {
        try
        {
            var bgColor = (Color)ColorConverter.ConvertFromString("#0B0606");
            var sidebarColor = (Color)ColorConverter.ConvertFromString("#2B0000");
            var cardColor = (Color)ColorConverter.ConvertFromString("#250000");
            var textPrimary = (Color)ColorConverter.ConvertFromString("#FF8C00");
            var textSecondary = (Color)ColorConverter.ConvertFromString("#FFB07A");
            var accent = (Color)ColorConverter.ConvertFromString("#FF4500");

            // Replace brushes in the resource dictionary (needed for DynamicResource bindings)
            Application.Current.Resources["BackgroundBrush"] = new SolidColorBrush(bgColor);
            Application.Current.Resources["SidebarBrush"] = new SolidColorBrush(sidebarColor);
            Application.Current.Resources["CardBrush"] = new SolidColorBrush(cardColor);
            Application.Current.Resources["TextPrimaryBrush"] = new SolidColorBrush(textPrimary);
            Application.Current.Resources["TextSecondaryBrush"] = new SolidColorBrush(textSecondary);
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["AccentBlueBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["AccentPinkBrush"] = new SolidColorBrush(accent);

            // Replace gradients
            var blueGrad = new LinearGradientBrush();
            blueGrad.StartPoint = new System.Windows.Point(0, 0);
            blueGrad.EndPoint = new System.Windows.Point(1, 1);
            blueGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#330000"), 0));
            blueGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#5A0000"), 1));
            Application.Current.Resources["BlueGradient"] = blueGrad;

            var purpleGrad = new LinearGradientBrush();
            purpleGrad.StartPoint = new System.Windows.Point(0, 0);
            purpleGrad.EndPoint = new System.Windows.Point(1, 1);
            purpleGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#5A0000"), 0));
            purpleGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#330000"), 1));
            Application.Current.Resources["PurpleGradient"] = purpleGrad;

            // Apply direct brushes for explicit elements
            var bgBrush = new SolidColorBrush(bgColor);
            var sidebarBrush = new SolidColorBrush(sidebarColor);
            var textPrimaryBrush = new SolidColorBrush(textPrimary);
            var textSecondaryBrush = new SolidColorBrush(textSecondary);
            var accentBrush = new SolidColorBrush(accent);

            this.Background = bgBrush;
            RootBorder.Background = bgBrush;
            if (SidebarBorder != null) SidebarBorder.Background = sidebarBrush;
            if (PageTitle != null) PageTitle.Foreground = textPrimaryBrush;
            if (StatusText != null) StatusText.Foreground = textSecondaryBrush;
            if (LiveText != null) LiveText.Foreground = accentBrush;

            if (LiveGlow != null)
            {
                LiveGlow.Color = accent;
                LiveGlow.Opacity = 0.95;
                LiveGlow.BlurRadius = 18;
            }

            // Make the border hellish
            try
            {
                var g = new LinearGradientBrush();
                g.StartPoint = new System.Windows.Point(0, 0);
                g.EndPoint = new System.Windows.Point(1, 1);
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#330000"), 0));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF4500"), 0.25));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#330000"), 0.5));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF4500"), 0.75));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#330000"), 1));
                RootBorder.BorderBrush = g;
            }
            catch { }

            try { LiveIndicator.Background = accentBrush; } catch { }
            try { if (StatusText != null) StatusText.Text = "Theme: Hell"; } catch { }
        }
        catch { }

        // Restore background transparency if custom background is active
        if (_isCustomBackgroundActive) RootBorder.Background = Brushes.Transparent;
    }

    private void ApplyLightTheme()
    {
        try
        {
            var bgColor = (Color)ColorConverter.ConvertFromString("#FAFAFA");
            var sidebarColor = (Color)ColorConverter.ConvertFromString("#FFFDF0");
            var cardColor = (Color)ColorConverter.ConvertFromString("#FFFFFF");
            var textPrimary = (Color)ColorConverter.ConvertFromString("#2A2A2A");
            var textSecondary = (Color)ColorConverter.ConvertFromString("#666666");
            var accent = (Color)ColorConverter.ConvertFromString("#FFD700");

            // Replace brushes in the resource dictionary (needed for DynamicResource bindings)
            Application.Current.Resources["BackgroundBrush"] = new SolidColorBrush(bgColor);
            Application.Current.Resources["SidebarBrush"] = new SolidColorBrush(sidebarColor);
            Application.Current.Resources["CardBrush"] = new SolidColorBrush(cardColor);
            Application.Current.Resources["TextPrimaryBrush"] = new SolidColorBrush(textPrimary);
            Application.Current.Resources["TextSecondaryBrush"] = new SolidColorBrush(textSecondary);
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["AccentBlueBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["AccentPinkBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE082"));

            // Replace gradients
            var blueGrad = new LinearGradientBrush();
            blueGrad.StartPoint = new System.Windows.Point(0, 0);
            blueGrad.EndPoint = new System.Windows.Point(1, 1);
            blueGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFF9C4"), 0));
            blueGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFFDE7"), 1));
            Application.Current.Resources["BlueGradient"] = blueGrad;

            var purpleGrad = new LinearGradientBrush();
            purpleGrad.StartPoint = new System.Windows.Point(0, 0);
            purpleGrad.EndPoint = new System.Windows.Point(1, 1);
            purpleGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFF176"), 0));
            purpleGrad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFF9C4"), 1));
            Application.Current.Resources["PurpleGradient"] = purpleGrad;

            // Apply direct brushes for explicit elements
            var bgBrush = new SolidColorBrush(bgColor);
            var sidebarBrush = new SolidColorBrush(sidebarColor);
            var textPrimaryBrush = new SolidColorBrush(textPrimary);
            var textSecondaryBrush = new SolidColorBrush(textSecondary);
            var accentBrush = new SolidColorBrush(accent);

            this.Background = bgBrush;
            RootBorder.Background = bgBrush;
            if (SidebarBorder != null) SidebarBorder.Background = sidebarBrush;
            if (PageTitle != null) PageTitle.Foreground = textPrimaryBrush;
            if (StatusText != null) StatusText.Foreground = textSecondaryBrush;
            if (LiveText != null) LiveText.Foreground = accentBrush;

            if (LiveGlow != null)
            {
                LiveGlow.Color = accent;
                LiveGlow.Opacity = 0.8;
                LiveGlow.BlurRadius = 10;
            }

            // Restore light border
            try
            {
                var g = new LinearGradientBrush();
                g.StartPoint = new System.Windows.Point(0, 0);
                g.EndPoint = new System.Windows.Point(1, 1);
                g.GradientStops.Add(new GradientStop(Colors.White, 0));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFD700"), 0.25));
                g.GradientStops.Add(new GradientStop(Colors.White, 0.5));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFD700"), 0.75));
                g.GradientStops.Add(new GradientStop(Colors.White, 1));
                RootBorder.BorderBrush = g;
            }
            catch { }

            try { LiveIndicator.Background = accentBrush; } catch { }
            try { if (StatusText != null) StatusText.Text = "Theme: Light"; } catch { }
        }
        catch { }

        // Restore background transparency if custom background is active
        if (_isCustomBackgroundActive) RootBorder.Background = Brushes.Transparent;
    }

    private void DynamicThemeToggle_Checked(object sender, RoutedEventArgs e)
    {
        _isDynamicThemeEnabled = DynamicThemeToggle.IsChecked == true;
        UpdateStatus(_isDynamicThemeEnabled ? "Dynamic theme enabled - UI will match biomes" : "Dynamic theme disabled");

        // Disable the static Hell/Light toggle when dynamic theme is active
        ThemeToggle.IsEnabled = !_isDynamicThemeEnabled;

        if (_isCustomBackgroundActive)
        {
            // If custom background is active, we don't want to apply solid backgrounds
            // But we might want to update text colors. 
            // For now, let's just re-apply the current theme to update colors, then force transparent background
            if (_isDynamicThemeEnabled) ApplyBiomeTheme(_currentThemeBiome);
            else if (ThemeToggle.IsChecked == true) ApplyHellTheme();
            else ApplyLightTheme();

            RootBorder.Background = Brushes.Transparent;
            return;
        }

        if (_isDynamicThemeEnabled && _currentThemeBiome != BiomeType.Normal)
        {
            // Apply current biome theme immediately
            ApplyBiomeTheme(_currentThemeBiome);
        }
        else if (!_isDynamicThemeEnabled)
        {
            // Return to default light theme (or hell if toggle is checked)
            if (ThemeToggle.IsChecked == true)
                ApplyHellTheme();
            else
                ApplyLightTheme();
        }
    }

    private void ApplyBiomeTheme(BiomeType biomeType)
    {
        if (!_isDynamicThemeEnabled) return;
        if (biomeType == _currentThemeBiome && biomeType != BiomeType.Normal) return; // Skip if same

        _currentThemeBiome = biomeType;
        var meta = BiomeDatabase.GetMetadata(biomeType);

        try
        {
            var primaryColor = (Color)ColorConverter.ConvertFromString(meta.Color);

            // Generate color scheme from biome color
            // Background: very dark version
            var bgColor = Color.FromRgb(
                (byte)(primaryColor.R * 0.08),
                (byte)(primaryColor.G * 0.08),
                (byte)(primaryColor.B * 0.08));

            // Sidebar: slightly lighter dark
            var sidebarColor = Color.FromRgb(
                (byte)(primaryColor.R * 0.15),
                (byte)(primaryColor.G * 0.15),
                (byte)(primaryColor.B * 0.15));

            // Card: dark with hint of color
            var cardColor = Color.FromRgb(
                (byte)(primaryColor.R * 0.12),
                (byte)(primaryColor.G * 0.12),
                (byte)(primaryColor.B * 0.12));

            // Text primary: bright tinted white
            var textPrimary = Color.FromRgb(
                (byte)(200 + primaryColor.R * 0.2),
                (byte)(200 + primaryColor.G * 0.2),
                (byte)(200 + primaryColor.B * 0.2));

            // Text secondary: muted version of accent
            var textSecondary = Color.FromRgb(
                (byte)(primaryColor.R * 0.7),
                (byte)(primaryColor.G * 0.7),
                (byte)(primaryColor.B * 0.7));

            // Replace brushes in resource dictionary
            Application.Current.Resources["BackgroundBrush"] = new SolidColorBrush(bgColor);
            Application.Current.Resources["SidebarBrush"] = new SolidColorBrush(sidebarColor);
            Application.Current.Resources["CardBrush"] = new SolidColorBrush(cardColor);
            Application.Current.Resources["TextPrimaryBrush"] = new SolidColorBrush(textPrimary);
            Application.Current.Resources["TextSecondaryBrush"] = new SolidColorBrush(textSecondary);
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["AccentBlueBrush"] = new SolidColorBrush(primaryColor);
            Application.Current.Resources["AccentPinkBrush"] = new SolidColorBrush(primaryColor);

            // Replace gradients
            var blueGrad = new LinearGradientBrush();
            blueGrad.StartPoint = new System.Windows.Point(0, 0);
            blueGrad.EndPoint = new System.Windows.Point(1, 1);
            blueGrad.GradientStops.Add(new GradientStop(Color.FromRgb(
                (byte)(primaryColor.R * 0.2), (byte)(primaryColor.G * 0.2), (byte)(primaryColor.B * 0.2)), 0));
            blueGrad.GradientStops.Add(new GradientStop(Color.FromRgb(
                (byte)(primaryColor.R * 0.35), (byte)(primaryColor.G * 0.35), (byte)(primaryColor.B * 0.35)), 1));
            Application.Current.Resources["BlueGradient"] = blueGrad;

            var purpleGrad = new LinearGradientBrush();
            purpleGrad.StartPoint = new System.Windows.Point(0, 0);
            purpleGrad.EndPoint = new System.Windows.Point(1, 1);
            purpleGrad.GradientStops.Add(new GradientStop(Color.FromRgb(
                (byte)(primaryColor.R * 0.35), (byte)(primaryColor.G * 0.35), (byte)(primaryColor.B * 0.35)), 0));
            purpleGrad.GradientStops.Add(new GradientStop(Color.FromRgb(
                (byte)(primaryColor.R * 0.2), (byte)(primaryColor.G * 0.2), (byte)(primaryColor.B * 0.2)), 1));
            Application.Current.Resources["PurpleGradient"] = purpleGrad;

            // Apply direct brushes for explicit elements
            var bgBrush = new SolidColorBrush(bgColor);
            var sidebarBrush = new SolidColorBrush(sidebarColor);
            var textPrimaryBrush = new SolidColorBrush(textPrimary);
            var textSecondaryBrush = new SolidColorBrush(textSecondary);
            var accentBrush = new SolidColorBrush(primaryColor);

            this.Background = bgBrush;
            RootBorder.Background = bgBrush;
            if (SidebarBorder != null) SidebarBorder.Background = sidebarBrush;
            if (PageTitle != null) PageTitle.Foreground = textPrimaryBrush;
            if (StatusText != null) StatusText.Foreground = textSecondaryBrush;
            if (LiveText != null) LiveText.Foreground = accentBrush;

            if (LiveGlow != null)
            {
                LiveGlow.Color = primaryColor;
                LiveGlow.Opacity = 0.9;
                LiveGlow.BlurRadius = 15;
            }

            // Animated border gradient
            try
            {
                var g = new LinearGradientBrush();
                g.StartPoint = new System.Windows.Point(0, 0);
                g.EndPoint = new System.Windows.Point(1, 1);
                g.GradientStops.Add(new GradientStop(bgColor, 0));
                g.GradientStops.Add(new GradientStop(primaryColor, 0.25));
                g.GradientStops.Add(new GradientStop(bgColor, 0.5));
                g.GradientStops.Add(new GradientStop(primaryColor, 0.75));
                g.GradientStops.Add(new GradientStop(bgColor, 1));
                RootBorder.BorderBrush = g;
            }
            catch { }

            try { LiveIndicator.Background = accentBrush; } catch { }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Theme error: {ex.Message}");
        }

        // Restore background transparency if custom background is active
        if (_isCustomBackgroundActive) RootBorder.Background = Brushes.Transparent;
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isMonitoring) StopMonitoring();
        else StartMonitoring();
    }

    private void StartMonitoring()
    {
        _isMonitoring = true;
        StartStopBtn.Content = "⏹  Stop Monitoring";
        StartStopIcon.Text = "⏹️";
        LiveText.Text = "LIVE";
        LiveIndicator.Background = new SolidColorBrush(Color.FromRgb(79, 195, 247));

        if (!string.IsNullOrWhiteSpace(WebhookUrlBox.Text))
            _discordWebhook?.SetWebhookUrl(WebhookUrlBox.Text.Trim());

        _instanceManager?.Start();

        if (_discordWebhook?.IsEnabled == true)
        {
            _ = _discordWebhook.SendCustomAlertAsync("Tracking Started", "System", "Macro monitoring is now active.", 0x4FC3F7);
        }

        // Auto-start fishing if enabled

    }

    private void StopMonitoring()
    {
        _isMonitoring = false;
        StartStopBtn.Content = "▶  Start Monitoring";
        StartStopIcon.Text = "▶️";
        LiveText.Text = "PAUSED";
        LiveIndicator.Background = new SolidColorBrush(Color.FromRgb(139, 139, 158));
        _instanceManager?.Stop();

        if (_discordWebhook?.IsEnabled == true)
        {
            _ = _discordWebhook.SendCustomAlertAsync("Tracking Stopped", "System", "Macro monitoring has been paused.", 0x8B8B9E);
        }


    }

    private void AlignWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_instanceManager != null)
        {
            try
            {
                WindowLayoutService.AlignWindows(_instanceManager.Instances.Keys);
                UpdateStatus("Aligned all windows to grid");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Align error: {ex.Message}");
            }
        }
    }

    private void OnInstanceAdded(InstanceInfo inst)
    {
        var vm = new InstanceViewModel(inst);
        Instances.Add(vm);
        NoInstancesPanel.Visibility = Visibility.Collapsed;

        if (_discordWebhook?.IsEnabled == true)
        {
            try
            {
                IntPtr hWnd = FindWindowForProcess(inst.Pid);
                byte[]? screenshot = ScreenshotHelper.CaptureWindow(hWnd);
                _ = _discordWebhook.SendCustomAlertAsync("Instance Found", inst.DisplayName, $"New process detected: {inst.ProcessName} (PID {inst.Pid})", 0x4FC3F7, screenshot);
            }
            catch { }
        }
    }

    private void OnInstanceRemoved(InstanceInfo inst)
    {
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null) Instances.Remove(vm);
        UpdateStats();
        NoInstancesPanel.Visibility = Instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_discordWebhook?.IsEnabled == true)
        {
            _ = _discordWebhook.SendCustomAlertAsync("Instance Lost", inst.DisplayName, $"Process closed (PID {inst.Pid})", 0x8B8B9E);
        }
    }

    private async void OnUsernameDetected(InstanceInfo inst)
    {
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null)
        {
            vm.DisplayName = inst.Username ?? inst.DisplayName;

            if (_avatarService != null && inst.Username != null)
            {
                try
                {
                    var avatarUrl = await _avatarService.GetAvatarUrlAsync(inst.Username);
                    if (avatarUrl != null)
                    {
                        inst.AvatarUrl = avatarUrl;
                        vm.AvatarUrl = avatarUrl;
                    }
                }
                catch { }
            }
        }
        UpdateStatus($"👤 {inst.Username}");
    }

    private void OnAuraChanged(InstanceInfo inst)
    {
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null) vm.CurrentAura = inst.CurrentAura;
        UpdateStatus($"✨ {inst.DisplayName}: {inst.CurrentAura}");
    }

    private async void OnMerchantDetected(InstanceInfo inst)
    {
        // Log merchant detection
        UpdateStatus($"🛒 MERCHANT DETECTED on {inst.DisplayName}!");

        // Update the view model
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null) vm.HasMerchant = true;

        // Add to history
        BiomeHistory.Insert(0, new BiomeHistoryItem
        {
            InstanceName = inst.DisplayName,
            BiomeName = "🛒 Mari Merchant",
            SpawnChance = "Rare NPC",
            DetectedAt = inst.MerchantDetectedAt,
            BiomeType = BiomeType.Normal,
            BiomeColor = (Color)ColorConverter.ConvertFromString("#FFD700")
        });
        HistoryCountText.Text = $" ({BiomeHistory.Count})";

        // Trigger Smart Alert
        TriggerAlert(null, "Merchant Detected!");

        // Send webhook notification
        if (_discordWebhook?.IsEnabled == true)
        {
            try
            {
                IntPtr hWnd = FindWindowForProcess(inst.Pid);
                byte[]? screenshot = ScreenshotHelper.CaptureWindow(hWnd);

                await _discordWebhook.SendCustomAlertAsync(
                    "Mari Merchant Detected",
                    inst.DisplayName,
                    "Mari the Traveling Merchant has arrived!",
                    0xFFD700, // Gold
                    screenshot
                );
            }
            catch { }
        }
    }

    private async void OnJesterDetected(InstanceInfo inst)
    {
        // Log jester detection
        UpdateStatus($"🃏 JESTER DETECTED on {inst.DisplayName}!");

        // Update the view model
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null) vm.HasJester = true;

        var meta = BiomeDatabase.GetMetadata(BiomeType.Normal); // NPC items use special colors anyway

        // Add to history
        BiomeHistory.Insert(0, new BiomeHistoryItem
        {
            InstanceName = inst.DisplayName,
            BiomeName = "🃏 Jester",
            SpawnChance = "Rare NPC",
            DetectedAt = inst.JesterDetectedAt,
            BiomeType = BiomeType.Normal,
            BiomeColor = (Color)ColorConverter.ConvertFromString("#9B59B6")
        });
        HistoryCountText.Text = $" ({BiomeHistory.Count})";

        // Trigger Smart Alert
        TriggerAlert(null, "Jester Detected!");

        // Send webhook notification
        if (_discordWebhook?.IsEnabled == true)
        {
            try
            {
                IntPtr hWnd = FindWindowForProcess(inst.Pid);
                byte[]? screenshot = ScreenshotHelper.CaptureWindow(hWnd);

                await _discordWebhook.SendCustomAlertAsync(
                    "Jester Detected",
                    inst.DisplayName,
                    "The Jester has appeared! Check your instance.",
                    0x9B59B6, // Purple
                    screenshot
                );
            }
            catch { }
        }
    }

    private async void OnEdenDetected(InstanceInfo inst)
    {
        UpdateStatus($"🌌 EDEN DETECTED on {inst.DisplayName}!");

        // Add to history
        BiomeHistory.Insert(0, new BiomeHistoryItem
        {
            InstanceName = inst.DisplayName,
            BiomeName = "🌌 Eden Detected",
            SpawnChance = "Event",
            DetectedAt = inst.EdenDetectedAt,
            BiomeType = BiomeType.Normal,
            BiomeColor = (Color)ColorConverter.ConvertFromString("#9932CC")
        });

        TriggerAlert(null, "Eden Spawn Detected!");

        if (_discordWebhook?.IsEnabled == true)
        {
            try
            {
                IntPtr hWnd = FindWindowForProcess(inst.Pid);
                byte[]? screenshot = ScreenshotHelper.CaptureWindow(hWnd);

                await _discordWebhook.SendCustomAlertAsync(
                    "Eden Detected",
                    inst.DisplayName,
                    "The Devourer of the Void has appeared!",
                    0x9932CC,
                    screenshot
                );
            }
            catch { }
        }
    }

    private async void OnBiomeChanged(InstanceInfo inst)
    {
        var vm = Instances.FirstOrDefault(i => i.Pid == inst.Pid);
        if (vm != null) vm.UpdateFromInstance(inst);

        // Auto Pop Logic: Use 'vok taran' in Glitch biome
        if (inst.CurrentBiome == "Glitch" || inst.CurrentBiome == "Glitched")
        {
            UpdateStatus($"Auto Pop: Triggering Glitch macro for {inst.DisplayName}");
            if (_inputService != null)
            {
                await _inputService.SendChatCommand(inst.Pid, "vok taran");
            }
        }

        _biomesSeenTotal++;
        var meta = BiomeDatabase.GetMetadata(inst.BiomeType);
        if (meta.Rarity >= 5) _raresFound++;

        // Add to history
        var historyItem = new BiomeHistoryItem
        {
            InstanceName = inst.DisplayName,
            BiomeName = inst.CurrentBiome,
            SpawnChance = meta.SpawnChance,
            DetectedAt = inst.BiomeDetectedAt,
            BiomeType = inst.BiomeType,
            BiomeColor = (Color)ColorConverter.ConvertFromString(meta.Color)
        };
        BiomeHistory.Insert(0, historyItem);

        HistoryCountText.Text = $" ({BiomeHistory.Count})";
        while (BiomeHistory.Count > 100) BiomeHistory.RemoveAt(BiomeHistory.Count - 1);

        UpdateHeroBiome(inst.CurrentBiome, inst.BiomeType);
        UpdateStats();
        UpdateStatus($"🌍 {inst.DisplayName}: {inst.CurrentBiome}");

        // Trigger Smart Alert
        if (meta.Rarity >= RarityThresholdSlider.Value)
        {
            TriggerAlert(inst.BiomeType);
        }

        // Apply dynamic biome theme if enabled
        if (_isDynamicThemeEnabled)
        {
            ApplyBiomeTheme(inst.BiomeType);
        }

        if (_discordWebhook?.IsEnabled == true)
        {
            try
            {
                var biomeInfo = new BiomeInfo
                {
                    Type = inst.BiomeType,
                    Name = inst.CurrentBiome,
                    DetectedAt = inst.BiomeDetectedAt,
                    Source = inst.DisplayName
                };

                IntPtr hWnd = FindWindowForProcess(inst.Pid);
                byte[]? screenshot = ScreenshotHelper.CaptureWindow(hWnd);

                await _discordWebhook.SendBiomeNotificationAsync(biomeInfo, screenshot);
            }
            catch { }
        }
    }

    private void TriggerAlert(BiomeType? type = null, string? customMsg = null)
    {
        if (AudioAlertToggle.IsChecked == true)
        {
            System.Media.SystemSounds.Exclamation.Play();
        }

        if (FlashAlertToggle.IsChecked == true)
        {
            try
            {
                IntPtr windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                fi.hwnd = windowHandle;
                fi.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
                fi.uCount = 5;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        string msg = customMsg ?? $"Rare Biome Found: {type}";
        UpdateStatus("🚨 " + msg);
    }

    private void Alert_Checked(object sender, RoutedEventArgs e)
    {
        // Visual feedback
    }

    private void RarityThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RarityThresholdLabel != null)
        {
            RarityThresholdLabel.Text = $"Rarity {(int)e.NewValue}+";
        }
    }

    private void UpdateHeroBiome(string biomeName, BiomeType type)
    {
        var meta = BiomeDatabase.GetMetadata(type);

        BigBiomeName.Text = biomeName;
        BiomeChanceText.Text = meta.SpawnChance;

        BigBiomeEmoji.Text = type switch
        {
            BiomeType.Normal => "🌍",
            BiomeType.Sandstorm => "🏜️",
            BiomeType.Hell => "🔥",
            BiomeType.Starfall => "⭐",
            BiomeType.Heaven => "☁️",
            BiomeType.Corruption => "💜",
            BiomeType.Null => "⬛",
            BiomeType.Glitched => "🟢",
            BiomeType.Dreamspace => "💭",
            BiomeType.Cyberspace => "💠",
            BiomeType.Windy => "💨",
            BiomeType.Snowy => "❄️",
            BiomeType.Rainy => "🌧️",
            BiomeType.PumpkinMoon => "🎃",
            BiomeType.Graveyard => "⚰️",
            BiomeType.BloodRain => "🩸",
            BiomeType.Aurora => "🌌",
            _ => "🌍"
        };

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(meta.Color);
            var darkerColor = Color.FromArgb(255, (byte)(color.R * 0.3), (byte)(color.G * 0.3), (byte)(color.B * 0.3));

            HeroGradient1.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(darkerColor, TimeSpan.FromMilliseconds(500)));
            HeroGradient2.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromRgb(26, 26, 36), TimeSpan.FromMilliseconds(500)));
            HeroGlow.Color = color;
        }
        catch { }
    }

    private void UpdateStats()
    {
        InstanceCountLabel.Text = Instances.Count.ToString();
        RaresFoundLabel.Text = _raresFound.ToString();
        InstanceCountText.Text = $"({Instances.Count})";
    }

    private void UpdateStatus(string message) => StatusText.Text = message;

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(WebhookUrlBox.Text))
        {
            MessageBox.Show("Please enter a webhook URL first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _discordWebhook?.SetWebhookUrl(WebhookUrlBox.Text.Trim());
        TestWebhookBtn.IsEnabled = false;
        TestWebhookBtn.Content = "...";

        try { await _discordWebhook!.SendTestMessageAsync(); }
        finally
        {
            TestWebhookBtn.IsEnabled = true;
            TestWebhookBtn.Content = "Test";
        }
    }

    private void AntiAfkToggle_Click(object sender, RoutedEventArgs e)
    {
        bool isEnabled = AntiAfkMainToggle.IsChecked == true;

        // Sync toggles just in case
        // if (AntiAfkMainToggle != null) AntiAfkMainToggle.IsChecked = isEnabled; // Already bound or clicked

        if (isEnabled)
        {
            _antiAfkService?.Start();
            AntiAfkStatusText.Text = "ACTIVE";
            AntiAfkStatusBadge.Background = new SolidColorBrush(Color.FromRgb(35, 134, 54)); // Green
        }
        else
        {
            _antiAfkService?.Stop();
            AntiAfkStatusText.Text = "INACTIVE";
            AntiAfkStatusBadge.Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)); // Grey
        }
    }




    #region Custom Background Support

    private bool _isCustomBackgroundActive = false;

    private void SelectBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Media Files|*.jpg;*.jpeg;*.png;*.mp4;*.gif;*.webm|Images|*.jpg;*.jpeg;*.png|Videos|*.mp4;*.gif;*.webm",
            Title = "Select Background Image or Video"
        };

        if (dialog.ShowDialog() == true)
        {
            SetBackground(dialog.FileName);
        }
    }

    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _isCustomBackgroundActive = false;

        // Stop media
        BackgroundMedia.Source = null;
        BackgroundMedia.Visibility = Visibility.Collapsed;

        // Clear image
        BackgroundImage.Source = null;
        BackgroundImage.Visibility = Visibility.Collapsed;

        // Restore theme opacity logic (Reset overlay dimmer)
        // BackgroundDimmer.Opacity = 0.5; // Keep default for next time

        // Restore RootBorder background from theme
        if (ThemeToggle.IsChecked == true) ApplyHellTheme();
        else ApplyLightTheme();

        UpdateStatus("Background reset to default theme");
    }

    private void SetBackground(string path)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLower();
            bool isVideo = ext == ".mp4" || ext == ".webm" || ext == ".gif"; // Treat GIF as media if possible, or Image

            if (isVideo && ext != ".gif") // MediaElement handles MP4
            {
                BackgroundMedia.Source = new Uri(path);
                BackgroundMedia.Visibility = Visibility.Visible;
                BackgroundMedia.Play();

                BackgroundImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                // For GIFs, standard Image control only shows first frame. 
                // Creating a simplified "GIF as Media" support via MediaElement usually works better for animated GIFs in WPF
                if (ext == ".gif")
                {
                    BackgroundMedia.Source = new Uri(path);
                    BackgroundMedia.Visibility = Visibility.Visible;
                    BackgroundMedia.Play();
                    BackgroundImage.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Static Image
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Lock file release
                    bitmap.EndInit();

                    BackgroundImage.Source = bitmap;
                    BackgroundImage.Visibility = Visibility.Visible;

                    BackgroundMedia.Source = null;
                    BackgroundMedia.Visibility = Visibility.Collapsed;
                }
            }

            _isCustomBackgroundActive = true;

            // Make root border transparent so background shows through
            RootBorder.Background = Brushes.Transparent;

            UpdateStatus("Custom background applied");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error setting background: {ex.Message}");
            // Reset on error
            ResetBackground_Click(null, null);
        }
    }

    private void BackgroundMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Loop video
        BackgroundMedia.Position = TimeSpan.Zero;
        BackgroundMedia.Play();
    }

    private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BackgroundDimmer == null) return;

        // Slider manages the dimmer opacity directly?
        // Actually, usually users want to dim the IMAGE, or dim the BACKGROUND (black overlay).
        // Let's assume the slider controls the VISIBILITY of the background (Opacity of the Image/Media).
        // BUT the UI text says "Opacity".

        // Let's map Slider 0..1 to Media/Image Opacity.
        // And maybe the Dimmer is fixed? Or usually "Background Dim" means "Darken the background".
        // Let's check my XAML: <Border Background="Black" Opacity="0.5" x:Name="BackgroundDimmer"/>

        // Let's make the slider control the DIMMER opacity (Darkness).
        // 0.0 = No Dim (Clear image)
        // 1.0 = Full Dim (Black)

        BackgroundDimmer.Opacity = BgOpacitySlider.Value;
        if (BgOpacityText != null) BgOpacityText.Text = $"{(int)(BgOpacitySlider.Value * 100)}%";
    }

    #endregion

    private void Interval_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_antiAfkService == null) return;

        if (int.TryParse(MinIntervalBox.Text, out int min) && int.TryParse(MaxIntervalBox.Text, out int max))
        {
            _antiAfkService.MinInterval = min;
            _antiAfkService.MaxInterval = max;
            // Reflect clamped values back
            MinIntervalBox.Text = _antiAfkService.MinInterval.ToString();
            MaxIntervalBox.Text = _antiAfkService.MaxInterval.ToString();
        }
    }

    private void Action_Checked(object sender, RoutedEventArgs e)
    {
        if (_antiAfkService == null) return;
        _antiAfkService.EnableJump = CheckJump.IsChecked == true;
        _antiAfkService.EnableWalk = CheckWalk.IsChecked == true;
        _antiAfkService.EnableSpin = CheckSpin.IsChecked == true;
    }

    private void ToggleGhostMode_Click(object sender, RoutedEventArgs e)
    {
        if (_instanceManager == null) return;

        _isGhostMode = !_isGhostMode;

        GhostModeText.Text = _isGhostMode ? "Ghost Mode (Show)" : "Ghost Mode (Hide)";

        var pids = _instanceManager.Instances.Keys.ToList();
        int count = 0;

        if (_isGhostMode)
        {
            // Hiding: Robustly find handles first, then hide
            _ghostHandles.Clear();
            foreach (var pid in pids)
            {
                try
                {
                    IntPtr hWnd = FindWindowForProcess(pid);
                    if (hWnd != IntPtr.Zero)
                    {
                        _ghostHandles[pid] = hWnd;
                        ShowWindow(hWnd, SW_HIDE);
                        count++;
                    }
                }
                catch { }
            }
            UpdateStatus($"Ghost Mode: Hidden {count} windows");
        }
        else
        {
            // Showing: Use cached handles or fallback to robust search
            foreach (var pid in pids)
            {
                try
                {
                    IntPtr hWnd = IntPtr.Zero;
                    if (_ghostHandles.ContainsKey(pid)) hWnd = _ghostHandles[pid];
                    else hWnd = FindWindowForProcess(pid, onlyVisible: false);

                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, SW_SHOW);
                        count++;
                    }
                }
                catch { }
            }
            _ghostHandles.Clear();
            UpdateStatus($"Ghost Mode: Restored {count} windows");
        }
    }

    private IntPtr FindWindowForProcess(int pid, bool onlyVisible = true)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid)
            {
                bool isVisible = IsWindowVisible(hWnd);
                if (!onlyVisible || isVisible)
                {
                    var sb = new System.Text.StringBuilder(256);
                    GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString();
                    if (title.Contains("Roblox") || title == "Roblox")
                    {
                        result = hWnd;
                        return false; // Found it, stop
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private void NavOptimizations_Click(object sender, RoutedEventArgs e) => SwitchPage("Optimizations");

    private void OptimizeMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_instanceManager == null) return;

        long bytesBefore = 0;
        long bytesAfter = 0;
        int count = 0;

        foreach (var pid in _instanceManager.Instances.Keys)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                bytesBefore += proc.WorkingSet64;
                EmptyWorkingSet(proc.Handle);
                proc.Refresh(); // Important to get new value
                bytesAfter += proc.WorkingSet64;
                count++;
            }
            catch { }
        }

        long saved = bytesBefore - bytesAfter;
        if (saved < 0) saved = 0; // Just in case
        string savedStr = FormatBytes(saved);

        UpdateStatus($"Trimmed RAM for {count} instances. Freed {savedStr}!");
        MessageBox.Show($"Successfully trimmed working set for {count} instances.\n\nMemory Freed: {savedStr}",
                        "Memory Optimization", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleEfficiencyMode_Click(object sender, RoutedEventArgs e)
    {
        if (_instanceManager == null) return;

        _isEfficiencyMode = EfficiencyModeToggle.IsChecked == true;
        uint priority = _isEfficiencyMode ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS;
        int count = 0;

        foreach (var pid in _instanceManager.Instances.Keys)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                SetPriorityClass(proc.Handle, priority);
                count++;
            }
            catch { }
        }

        UpdateStatus(_isEfficiencyMode ? $"Efficiency Mode: Enabled (Low Priority) for {count} instances"
                                           : $"Efficiency Mode: Disabled (Normal Priority) for {count} instances");
    }

    private void CpuLimitToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.Tag is int pid && _cpuLimiter != null)
        {
            // Find ViewModel to get slider value
            var vm = Instances.FirstOrDefault(x => x.Pid == pid);
            if (vm == null) return;

            if (toggle.IsChecked == true)
            {
                if (vm.IsAutoCpuLimited) _cpuLimiter.StartAutoLimiting(pid);
                else _cpuLimiter.StartLimiting(pid, vm.CpuLimitValue);

                UpdateStatus($"CPU Limiter: Active on PID {pid}");
            }
            else
            {
                _cpuLimiter.StopLimiting(pid);
                UpdateStatus($"CPU Limiter: Stopped on PID {pid}");
            }
        }
    }

    private void AutoLimitToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.Tag is int pid && _cpuLimiter != null)
        {
            var vm = Instances.FirstOrDefault(x => x.Pid == pid);
            if (vm == null) return;

            if (toggle.IsChecked == true)
            {
                // Auto Enabled: Force Limiter On if it wasn't
                vm.IsCpuLimited = true;
                _cpuLimiter.StartAutoLimiting(pid);
                UpdateStatus($"CPU Limiter: Auto Mode Active on PID {pid}");
            }
            else
            {
                // Auto Disabled: Revert to manual if still enabled
                if (vm.IsCpuLimited)
                {
                    _cpuLimiter.StartLimiting(pid, vm.CpuLimitValue);
                    UpdateStatus($"CPU Limiter: Manual Mode Active on PID {pid}");
                }
            }
        }
    }

    private void CpuLimitSlider_Changed(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Slider slider && slider.Tag is int pid && _cpuLimiter != null)
        {
            var vm = Instances.FirstOrDefault(x => x.Pid == pid);
            if (vm != null && vm.IsCpuLimited)
            {
                // Update active limiter
                _cpuLimiter.StartLimiting(pid, vm.CpuLimitValue);
            }
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }
        return String.Format("{0:0.##} {1}", dblSByte, suffix[i]);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        BiomeHistory.Clear();
        HistoryCountText.Text = " (0)";
        _biomesSeenTotal = 0;
        _raresFound = 0;
        UpdateStats();
        UpdateStatus("History cleared");
    }

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            this.DragMove();
    }

    private void AutoCleanToggle_Checked(object sender, RoutedEventArgs e)
    {
        bool enabled = (sender as ToggleButton)?.IsChecked == true;
        if (_memoryOptimizer == null) return;

        if (enabled)
        {
            _memoryOptimizer.StartMonitoring();
            UpdateStatus("Auto Memory Cleaner: Enabled");
        }
        else
        {
            _memoryOptimizer.StopMonitoring();
            UpdateStatus("Auto Memory Cleaner: Disabled");
        }
    }

    private void MemoryThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_memoryOptimizer == null || MemoryThresholdText == null) return;

        int threshold = (int)e.NewValue;
        _memoryOptimizer.UpdateThreshold(threshold);
        MemoryThresholdText.Text = $"Trigger at {threshold}% RAM";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        StopMonitoring();


        _instanceManager?.Dispose();
        _discordWebhook?.Dispose();
        _avatarService?.Dispose();
        _cpuLimiter?.Dispose();
        _memoryOptimizer?.Dispose();
        base.OnClosed(e);
    }
}

public class InstanceViewModel : INotifyPropertyChanged
{
    public int Pid { get; }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(nameof(DisplayName)); }
    }

    private string _currentBiome = "Normal";
    public string CurrentBiome
    {
        get => _currentBiome;
        set { _currentBiome = value; OnPropertyChanged(nameof(CurrentBiome)); }
    }

    private string? _currentAura;
    public string? CurrentAura
    {
        get => _currentAura;
        set { _currentAura = value; OnPropertyChanged(nameof(CurrentAura)); OnPropertyChanged(nameof(HasAura)); }
    }

    public bool HasAura => !string.IsNullOrEmpty(_currentAura);

    private bool _hasMerchant;
    public bool HasMerchant
    {
        get => _hasMerchant;
        set { _hasMerchant = value; OnPropertyChanged(nameof(HasMerchant)); }
    }

    private bool _hasJester;
    public bool HasJester
    {
        get => _hasJester;
        set { _hasJester = value; OnPropertyChanged(nameof(HasJester)); }
    }

    // Using SolidColorBrush for animation support
    private SolidColorBrush _biomeBrush = new(Colors.LimeGreen);
    public SolidColorBrush BiomeBrush
    {
        get => _biomeBrush;
        set { _biomeBrush = value; OnPropertyChanged(nameof(BiomeBrush)); }
    }

    // Keeping raw Color property for things that might need it (like DropShadow, though it might snap)
    private Color _biomeColor = Colors.LimeGreen;
    public Color BiomeColor
    {
        get => _biomeColor;
        set { _biomeColor = value; OnPropertyChanged(nameof(BiomeColor)); }
    }

    private string? _avatarUrl;
    public string? AvatarUrl
    {
        get => _avatarUrl;
        set { _avatarUrl = value; OnPropertyChanged(nameof(AvatarUrl)); OnPropertyChanged(nameof(HasAvatar)); OnPropertyChanged(nameof(AvatarImage)); }
    }

    public bool HasAvatar => !string.IsNullOrEmpty(_avatarUrl);

    public ImageSource? AvatarImage
    {
        get
        {
            if (string.IsNullOrEmpty(_avatarUrl)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_avatarUrl);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch { return null; }
        }
    }

    public InstanceViewModel(InstanceInfo inst)
    {
        Pid = inst.Pid;
        DisplayName = inst.Username ?? inst.DisplayName;
        AvatarUrl = inst.AvatarUrl;
        UpdateFromInstance(inst);
    }

    public void UpdateFromInstance(InstanceInfo inst)
    {
        if (inst.Username != null) DisplayName = inst.Username;
        CurrentBiome = inst.CurrentBiome;
        CurrentAura = inst.CurrentAura;
        HasMerchant = inst.MerchantDetected;
        HasJester = inst.JesterDetected;
        if (inst.AvatarUrl != null && AvatarUrl != inst.AvatarUrl) AvatarUrl = inst.AvatarUrl;

        var meta = BiomeDatabase.GetMetadata(inst.BiomeType);
        Color newColor;
        try { newColor = (Color)ColorConverter.ConvertFromString(meta.Color); }
        catch { newColor = Colors.LimeGreen; }

        if (newColor != BiomeColor)
        {
            // Animate to new color
            var oldColor = BiomeColor;
            BiomeColor = newColor; // Update underlying property immediately or keep it for shadow reference

            // Create animation on the Brush
            // Ensure we are on UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                var animation = new ColorAnimation
                {
                    From = oldColor,
                    To = newColor,
                    Duration = TimeSpan.FromSeconds(0.6),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                BiomeBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            });
        }
    }

    private bool _isCpuLimited;
    public bool IsCpuLimited
    {
        get => _isCpuLimited;
        set { _isCpuLimited = value; OnPropertyChanged(nameof(IsCpuLimited)); }
    }

    private bool _isAutoCpuLimited;
    public bool IsAutoCpuLimited
    {
        get => _isAutoCpuLimited;
        set
        {
            _isAutoCpuLimited = value;
            OnPropertyChanged(nameof(IsAutoCpuLimited));
            // When Auto is on, manual limit is implied but disabled in UI
        }
    }

    private int _cpuLimitValue = 50; // Default 50%
    public int CpuLimitValue
    {
        get => _cpuLimitValue;
        set { _cpuLimitValue = value; OnPropertyChanged(nameof(CpuLimitValue)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BiomeHistoryItem
{
    public string InstanceName { get; set; } = "";
    public string BiomeName { get; set; } = "";
    public string SpawnChance { get; set; } = "";
    public DateTime DetectedAt { get; set; }
    public BiomeType BiomeType { get; set; } = BiomeType.Normal;
    public Color BiomeColor { get; set; } = Colors.Gray;
}

