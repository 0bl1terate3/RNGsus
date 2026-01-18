using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiomeMacro.Services;

/// <summary>
/// Manages multiple Roblox instances and tracks biome per window.
/// </summary>
public class MultiInstanceManager : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, InstanceInfo> _instances = new();
    private readonly List<string> _logDirs;
    private Task? _detectionTask;
    
    public event Action<InstanceInfo>? OnBiomeChanged;
    public event Action<InstanceInfo>? OnInstanceAdded;
    public event Action<InstanceInfo>? OnInstanceRemoved;
    public event Action<InstanceInfo>? OnMerchantDetected;
    public event Action<InstanceInfo>? OnJesterDetected;
    public event Action<InstanceInfo>? OnEdenDetected;
    public event Action<string>? OnStatus;
    public event Action<string>? OnError;
    
    public IReadOnlyDictionary<int, InstanceInfo> Instances => _instances;
    public bool IsRunning { get; private set; }
    
    public MultiInstanceManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDirs = new List<string>
        {
            Path.Combine(localAppData, "Roblox", "logs"),
            Path.Combine(localAppData, "Bloxstrap", "Logs"),
            Path.Combine(localAppData, "Voidstrap", "Logs")
        };
    }
    
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        
        ApplyFastFlags();
        
        _detectionTask = Task.Run(DetectionLoop, _cts.Token);
        OnStatus?.Invoke("Multi-instance detection started");
    }
    
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts.Cancel();
        OnStatus?.Invoke("Detection stopped");
    }
    
    /// <summary>
    /// Apply FastFlags to enable biome detection in Roblox logs.
    /// </summary>
    private void ApplyFastFlags()
    {
        try
        {
            var versionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "Versions"
            );
            
            if (!Directory.Exists(versionsDir))
            {
                OnStatus?.Invoke("Roblox not found - FastFlags not applied");
                return;
            }
            
            var versionFolders = Directory.GetDirectories(versionsDir)
                .Where(d => Path.GetFileName(d).StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            if (versionFolders.Count == 0) return;
            
            // FastFlags needed for biome detection via BloxstrapRPC
            var flags = new Dictionary<string, object>
            {
                ["DFFlagDebugPerfMode"] = "True",
                ["FFlagHandleAltEnterFullscreenManually"] = "False",
                ["FStringDebugLuaLogPattern"] = "ExpChat/mountClientApp",
                ["FStringDebugLuaLogLevel"] = "trace"
            };
            
            int patched = 0;
            foreach (var ver in versionFolders)
            {
                var settingsDir = Path.Combine(ver, "ClientSettings");
                var settingsPath = Path.Combine(settingsDir, "ClientAppSettings.json");
                
                try
                {
                    Directory.CreateDirectory(settingsDir);
                    
                    Dictionary<string, object> existing = new();
                    if (File.Exists(settingsPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(settingsPath);
                            existing = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
                        }
                        catch { }
                    }
                    
                    bool needsUpdate = false;
                    foreach (var (key, val) in flags)
                    {
                        if (!existing.TryGetValue(key, out var existingVal) || 
                            existingVal?.ToString() != val.ToString())
                        {
                            needsUpdate = true;
                            existing[key] = val;
                        }
                    }
                    
                    if (needsUpdate)
                    {
                        File.WriteAllText(settingsPath, 
                            JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true }));
                        patched++;
                    }
                }
                catch { }
            }
            
            if (patched > 0)
            {
                OnStatus?.Invoke($"FastFlags applied to {patched} Roblox version(s) - Restart Roblox for biome detection!");
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"FastFlags error: {ex.Message}");
        }
    }
    
    private async Task DetectionLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 1. Find all Roblox processes
                RefreshInstances();
                
                // 2. Assign log files to instances
                AssignLogsToInstances();
                
                // 3. Parse username and biome from each instance's log
                foreach (var (pid, inst) in _instances.ToList())
                {
                    ParseUsernameFromLog(inst);
                    ParseLogUpdates(inst);
                }
                
                await Task.Delay(2000, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Detection error: {ex.Message}");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }
    
    private void RefreshInstances()
    {
        var robloxProcesses = Process.GetProcessesByName("RobloxPlayerBeta")
            .Concat(Process.GetProcessesByName("Windows10Universal"))
            .ToList();
        
        // Track current PIDs
        var currentPids = new HashSet<int>(robloxProcesses.Select(p => p.Id));
        
        // Remove dead instances
        foreach (var pid in _instances.Keys.ToList())
        {
            if (!currentPids.Contains(pid))
            {
                var inst = _instances[pid];
                _instances.Remove(pid);
                OnInstanceRemoved?.Invoke(inst);
                OnStatus?.Invoke($"Instance {inst.DisplayName} closed");
            }
        }
        
        // Add new instances
        foreach (var proc in robloxProcesses)
        {
            if (!_instances.ContainsKey(proc.Id))
            {
                var inst = new InstanceInfo
                {
                    Pid = proc.Id,
                    ProcessName = proc.ProcessName,
                    DisplayName = $"Instance {proc.Id}",
                    StartTime = GetProcessStartTime(proc)
                };
                _instances[proc.Id] = inst;
                OnInstanceAdded?.Invoke(inst);
                OnStatus?.Invoke($"Found new instance: PID {proc.Id}");
            }
        }
    }
    
    private DateTime GetProcessStartTime(Process proc)
    {
        try { return proc.StartTime; }
        catch { return DateTime.Now; }
    }
    
    private void AssignLogsToInstances()
    {
        // Get all log files sorted by modification time (newest first)
        var logFiles = new List<(string Path, DateTime Modified, DateTime Created)>();
        
        foreach (var dir in _logDirs)
        {
            if (!Directory.Exists(dir)) continue;
            
            foreach (var file in Directory.GetFiles(dir, "*.log"))
            {
                try
                {
                    var fi = new FileInfo(file);
                    logFiles.Add((file, fi.LastWriteTime, fi.CreationTime));
                }
                catch { }
            }
        }
        
        logFiles = logFiles.OrderByDescending(x => x.Modified).ToList();
        
        // Keep track of assigned logs
        var assignedLogs = new HashSet<string>(
            _instances.Values.Where(i => i.LogFile != null).Select(i => i.LogFile!),
            StringComparer.OrdinalIgnoreCase
        );
        
        foreach (var (pid, inst) in _instances)
        {
            // Skip if already has valid log
            if (inst.LogFile != null && File.Exists(inst.LogFile))
                continue;
            
            // Strategy 1: Match hex PID in log filename (e.g., Player_5E5C_)
            var pidHexUpper = pid.ToString("X");
            var pidHexLower = pid.ToString("x");
            
            foreach (var (logPath, _, _) in logFiles.Take(100))
            {
                if (assignedLogs.Contains(logPath)) continue;
                
                var fileName = Path.GetFileName(logPath);
                // Check if hex PID appears in filename (case-insensitive)
                if (fileName.Contains($"_{pidHexUpper}_", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains($"_{pidHexLower}_", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains($"_{pidHexUpper}.", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains($"_{pidHexLower}.", StringComparison.OrdinalIgnoreCase))
                {
                    inst.LogFile = logPath;
                    inst.LogPosition = 0;
                    assignedLogs.Add(logPath);
                    OnStatus?.Invoke($"Assigned log to PID {pid} (filename match)");
                    break;
                }
            }
            
            if (inst.LogFile != null) continue;
            
            // Strategy 2: Direct PID match in log content
            foreach (var (logPath, _, _) in logFiles.Take(50))
            {
                if (assignedLogs.Contains(logPath)) continue;
                
                try
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    
                    // Read first 32KB
                    var buffer = new char[32768];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    var header = new string(buffer, 0, read);
                    
                    // Check various PID patterns
                    if (header.Contains($"pid:{pid}") ||
                        header.Contains($"PID: {pid}") ||
                        header.Contains($",{pidHexLower},") ||
                        header.Contains($" {pid} "))
                    {
                        inst.LogFile = logPath;
                        inst.LogPosition = 0;
                        assignedLogs.Add(logPath);
                        OnStatus?.Invoke($"Assigned log to PID {pid} (content match)");
                        break;
                    }
                }
                catch { }
            }
            
            // Strategy 3: Match by process start time vs log creation time (±120s for longer window)
            if (inst.LogFile == null && inst.StartTime != default)
            {
                // Sort by closest time match
                var candidates = logFiles
                    .Where(l => !assignedLogs.Contains(l.Path))
                    .Select(l => (l.Path, Diff: Math.Abs((l.Created - inst.StartTime).TotalSeconds)))
                    .Where(l => l.Diff <= 120)
                    .OrderBy(l => l.Diff)
                    .ToList();
                
                if (candidates.Count > 0)
                {
                    inst.LogFile = candidates[0].Path;
                    inst.LogPosition = 0;
                    assignedLogs.Add(candidates[0].Path);
                    OnStatus?.Invoke($"Assigned log to PID {pid} (time match: {candidates[0].Diff:F0}s)");
                }
            }
        }
    }
    
    // Regex patterns for biome detection (from Python implementation)
    private static readonly Regex[] BiomePatterns = new[]
    {
        // BloxstrapRPC format (Sol's RNG uses this)
        new Regex(@"""largeImage"":\{""hoverText"":""([^""]+)""", RegexOptions.Compiled),
        new Regex(@"""biome"":\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"(?:Biome|biome|BIOME)[:\s]+([A-Z\s]+)", RegexOptions.Compiled),
        new Regex(@"(?:Changed to|changed to)\s+([A-Z\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"BIOME_CHANGED\s+([A-Z\s]+)", RegexOptions.Compiled)
    };
    
    // Aura detection patterns
    private static readonly Regex AuraStatePattern = new(
        @"""state"":""Equipped \\""([^""]+)\\""""", RegexOptions.Compiled);
    private static readonly Regex AuraHoverPattern = new(
        @"""hoverText"":""Aura:\s*([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex AuraEquippedPattern = new(
        @"Equipped\s+['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Merchant detection patterns (Mari the Traveling Merchant)
    private static readonly Regex[] MerchantPatterns = new[]
    {
        new Regex(@"Mari.*has\s+arrived", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Traveling\s+Merchant.*arrived", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"""hoverText"":""Mari""", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Merchant\s+(?:has\s+)?(?:spawned|appeared)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Mari.*spawn", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };
    
    // Jester NPC detection patterns
    private static readonly Regex[] JesterPatterns = new[]
    {
        new Regex(@"Jester.*has\s+arrived", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Jester.*spawn", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"""hoverText"":""Jester""", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Jester\s+(?:has\s+)?(?:spawned|appeared)", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    };

    // Eden detection patterns (SolsScope)
    private static readonly Regex EdenPattern = new(
        @"The Devourer of the Void, <b>(.*?)</b> has appeared somewhere in <i>(.*?)</i>\.", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Helper to extract timestamp from log line
    private static readonly Regex LogTimestampPattern = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)", RegexOptions.Compiled);
    
    // Username detection patterns (from Python implementation)
    private static readonly Regex[] UsernamePatterns = new[]
    {
        new Regex(@"displayName["":\s]+([A-Za-z0-9_]{3,20})", RegexOptions.Compiled),
        new Regex(@"Players\.([A-Za-z0-9_]{3,20})(?:[^A-Za-z0-9_]|$)", RegexOptions.Compiled),
        new Regex(@"""name"":""([A-Za-z0-9_]{3,20})""", RegexOptions.Compiled),
        new Regex(@"user:\s*([A-Za-z0-9_]{3,20})", RegexOptions.Compiled),
        new Regex(@"Player\s+([A-Za-z0-9_]{3,20})\s+added", RegexOptions.Compiled)
    };
    
    private static readonly HashSet<string> ExcludedUsernames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PlayerScripts", "PlayerGui", "PlayerModule", "Players", "LocalPlayer",
        "HumanoidRootPart", "Humanoid", "Character", "LocalScript", "Workspace",
        "Camera", "Sound", "Animation", "Animator", "Backpack", "StarterGui",
        "ReplicatedStorage", "ReplicatedFirst", "ServerStorage", "ServerScriptService",
        "Head", "Torso", "RightArm", "LeftArm", "RightLeg", "LeftLeg",
        "http", "https", "www", "com", "org", "net", "roblox", "html", "json",
        // Roblox internal service names that appear in logs
        "CaptureStorage", "Capture", "Storage", "RobloxStorage", "LocalStorage",
        "SoundService", "TeleportService", "RunService", "UserInputService",
        "ContentProvider", "CoreGui", "CorePackages", "Packages", "JoinScript",
        "DataStoreService", "MarketplaceService", "PolicyService", "MemStorageService",
        "HttpService", "Stats", "Plugin", "Selection", "DataModel", "RenderStepped",
        "ScriptContext", "LogService", "NetworkClient", "NetworkServer", "Visit"
    };
    
    public event Action<InstanceInfo>? OnAuraChanged;
    public event Action<InstanceInfo>? OnUsernameDetected;
    
    private void ParseUsernameFromLog(InstanceInfo inst)
    {
        // Retry username detection if log file was recently assigned (LogPosition == 0)
        if (inst.Username != null && inst.LogPosition > 0)
            return;
        
        // Try to find username in the assigned log file first
        if (!string.IsNullOrEmpty(inst.LogFile) && File.Exists(inst.LogFile))
        {
            var username = ExtractUsernameFromLogFile(inst.LogFile);
            if (username != null)
            {
                inst.Username = username;
                inst.DisplayName = username;
                OnUsernameDetected?.Invoke(inst);
                return;
            }
        }
        
        // Fallback: Scan ALL Roblox log files for username (handles Voidstrap case)
        // Match by process start time to find the corresponding standard Roblox log
        if (inst.StartTime != default)
        {
            var robloxLogsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "logs"
            );
            
            if (Directory.Exists(robloxLogsDir))
            {
                var logFiles = Directory.GetFiles(robloxLogsDir, "*.log")
                    .Select(f => new FileInfo(f))
                    .Where(f => Math.Abs((f.CreationTime - inst.StartTime).TotalSeconds) <= 120)
                    .OrderBy(f => Math.Abs((f.CreationTime - inst.StartTime).TotalSeconds))
                    .Take(3)
                    .ToList();
                
                foreach (var logFile in logFiles)
                {
                    var username = ExtractUsernameFromLogFile(logFile.FullName);
                    if (username != null)
                    {
                        inst.Username = username;
                        inst.DisplayName = username;
                        // Important: Also assign this as the Roblox log file for biome/NPC detection
                        if (string.IsNullOrEmpty(inst.RobloxLogFile))
                        {
                            inst.RobloxLogFile = logFile.FullName;
                            inst.RobloxLogPosition = 0;
                        }
                        OnUsernameDetected?.Invoke(inst);
                        OnStatus?.Invoke($"Found username {username} from Roblox log: {logFile.Name}");
                        return;
                    }
                }
            }
        }
    }
    
    private string? ExtractUsernameFromLogFile(string logPath)
    {
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            
            // Read up to 2MB for username detection (username can appear deep in logs)
            var buffer = new char[2097152];
            int read = reader.Read(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);
            
            // Priority 1: Look for authentication/join context (most reliable)
            // Pattern: "universeName":"Sol's RNG" with "userId" and "displayName" nearby
            var authMatch = Regex.Match(content, @"""displayName""\s*:\s*""([A-Za-z0-9_]{3,20})""");
            if (authMatch.Success)
            {
                var name = authMatch.Groups[1].Value;
                if (!ExcludedUsernames.Contains(name) && !name.All(char.IsDigit))
                    return name;
            }
            
            // Priority 2: Look for join message patterns
            var joinMatch = Regex.Match(content, @"Player\s+([A-Za-z0-9_]{3,20})\s+(?:joined|added|entered)", RegexOptions.IgnoreCase);
            if (joinMatch.Success)
            {
                var name = joinMatch.Groups[1].Value;
                if (!ExcludedUsernames.Contains(name) && !name.All(char.IsDigit))
                    return name;
            }
            
            // Priority 3: Players.XXX pattern but with better filtering
            // Look for Players.Username in a character/player context, not service context
            var playerMatches = Regex.Matches(content, @"Players\.([A-Za-z0-9_]{3,20})(?:[^A-Za-z0-9_]|$)");
            foreach (Match m in playerMatches)
            {
                var name = m.Groups[1].Value;
                // Skip if it's a service name or excluded
                if (ExcludedUsernames.Contains(name))
                    continue;
                // Skip if it ends with "Service", "Storage", "Script", etc.
                if (name.EndsWith("Service") || name.EndsWith("Storage") || 
                    name.EndsWith("Script") || name.EndsWith("Module") ||
                    name.EndsWith("Client") || name.EndsWith("Server") ||
                    name.EndsWith("Provider") || name.EndsWith("Gui"))
                    continue;
                // Skip purely numeric
                if (name.All(char.IsDigit))
                    continue;
                return name;
            }
            
            // Priority 4: Try other patterns as fallback
            string? fallbackNumeric = null;

            foreach (var pattern in UsernamePatterns)
            {
                var matches = pattern.Matches(content);
                foreach (Match m in matches)
                {
                    var name = m.Groups[1].Value;
                    if (name.Length >= 3 && !ExcludedUsernames.Contains(name))
                    {
                        // Skip names that look like services
                        if (name.EndsWith("Service") || name.EndsWith("Storage") || 
                            name.EndsWith("Script") || name.EndsWith("Module"))
                            continue;
                        
                        // Avoid purely numeric strings (likely UserIds) unless it's our only option
                        bool isNumeric = name.All(char.IsDigit);
                        if (!isNumeric)
                            return name;
                        
                        fallbackNumeric ??= name;
                    }
                }
            }

            return fallbackNumeric;
        }
        catch { }
        return null;
    }
    
    private void ParseLogUpdates(InstanceInfo inst)
    {
        // Identify correct log file
        EnsureLogFileAssigned(inst);
        
        var logFile = inst.RobloxLogFile ?? inst.LogFile;
        if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile)) return;

        // SAFE READ STRATEGY: Copy to temp, read lines, delete temp.
        // This avoids file locking issues and race conditions.
        var lines = ReadLogLinesSafe(logFile, 2000); // Read last 2000 lines
        if (lines.Count == 0) return;

        // Process State (Biome / Aura) - Scan REVERSE (Newest first)
        // We only care about the *latest* state.
        bool biomeFound = false;
        bool auraFound = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            
            // Biome
            if (!biomeFound)
            {
                foreach (var pattern in BiomePatterns)
                {
                    var m = pattern.Match(line);
                    if (m.Success)
                    {
                        var foundBiome = m.Groups[1].Value.Trim().ToUpperInvariant();
                        var biomeType = Models.BiomeDatabase.ParseFromString(foundBiome);
                        if (biomeType != Models.BiomeType.Unknown)
                        {
                            var realBiomeName = Models.BiomeDatabase.GetMetadata(biomeType).DisplayName;
                            
                            if (realBiomeName != inst.CurrentBiome)
                            {
                                inst.CurrentBiome = realBiomeName;
                                inst.BiomeDetectedAt = DateTime.Now;
                                inst.BiomeType = biomeType;
                                OnBiomeChanged?.Invoke(inst);
                                
                                // Reset NPC flags on biome change
                                inst.MerchantDetected = false;
                                inst.JesterDetected = false;
                                inst.EdenDetected = false;
                            }
                            biomeFound = true; // Stop searching for biome
                        }
                        break; // Found a match in this line, stop checking other patterns
                    }
                }
            }

            // Aura
            if (!auraFound)
            {
                // Try patterns (State, Hover, Equipped)
                var m1 = AuraStatePattern.Match(line);
                var m2 = AuraHoverPattern.Match(line);
                var m3 = AuraEquippedPattern.Match(line);
                
                string? detectedAura = null;
                if (m1.Success) detectedAura = m1.Groups[1].Value;
                else if (m2.Success) detectedAura = m2.Groups[1].Value;
                else if (m3.Success) detectedAura = m3.Groups[1].Value;

                if (detectedAura != null)
                {
                    detectedAura = detectedAura.Replace("_", ": "); // Fix formatting from SolsScope logic
                    if (detectedAura != inst.CurrentAura)
                    {
                        inst.CurrentAura = detectedAura;
                        OnAuraChanged?.Invoke(inst);
                    }
                    auraFound = true;
                }
            }

            if (biomeFound && auraFound) break; // Optimization: Found both latest states
        }

        // Process Events (Merchant, Eden, Jester) - Scan REVERSE but check TIMESTAMPS
        // We need to ensure we don't re-trigger for old events.
        
        // Find the LATEST event in the log that is newer than our last check
        DateTime lastEventTime = inst.LastLogEventTime;
        DateTime maxNewTime = lastEventTime;

        foreach (var line in lines) // iterating newest to oldest
        {
            var tsMatch = LogTimestampPattern.Match(line);
            if (!tsMatch.Success) continue;
            
            if (!DateTime.TryParseExact(tsMatch.Groups[1].Value, "yyyy-MM-ddTHH:mm:ss.fffZ", 
                System.Globalization.CultureInfo.InvariantCulture, 
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var logTime))
                continue;

            // Update max time tracked
            if (logTime > maxNewTime) maxNewTime = logTime;

            // If this line is older than our last processed event, stop processing events
            // (Since we are iterating backwards, all subsequent lines will be even older)
            if (logTime <= inst.LastLogEventTime) break;

            // --- Check Events ---
            
            // Merchant (Mari) - Exact patterns from reference macro (Coteab.Macro)
            if (!inst.MerchantDetected)
            {
                // Primary: Exact match from game logs
                if (line.Contains("[Merchant]: Mari has arrived on the island", StringComparison.OrdinalIgnoreCase))
                {
                     inst.MerchantDetected = true;
                     inst.MerchantDetectedAt = DateTime.Now;
                     OnMerchantDetected?.Invoke(inst);
                     OnStatus?.Invoke($"🛒 Mari Merchant Detected: {inst.DisplayName}");
                }
                // Fallback: General merchant patterns
                else if (line.Contains("[Merchant]:", StringComparison.OrdinalIgnoreCase) && 
                         line.Contains("Mari", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("has arrived", StringComparison.OrdinalIgnoreCase))
                {
                     inst.MerchantDetected = true;
                     inst.MerchantDetectedAt = DateTime.Now;
                     OnMerchantDetected?.Invoke(inst);
                     OnStatus?.Invoke($"🛒 Mari Merchant Detected (Fallback): {inst.DisplayName}");
                }
                // Regex fallback
                else if (MerchantPatterns.Any(p => p.IsMatch(line)))
                {
                     inst.MerchantDetected = true;
                     inst.MerchantDetectedAt = DateTime.Now;
                     OnMerchantDetected?.Invoke(inst);
                     OnStatus?.Invoke($"🛒 Merchant Detected (Regex): {inst.DisplayName}");
                }
            }

            // Jester - Exact patterns from reference macro (Coteab.Macro)
            if (!inst.JesterDetected)
            {
                // Primary: Exact match from game logs
                if (line.Contains("[Merchant]: Jester has arrived on the island", StringComparison.OrdinalIgnoreCase))
                {
                    inst.JesterDetected = true;
                    inst.JesterDetectedAt = DateTime.Now;
                    OnJesterDetected?.Invoke(inst);
                    OnStatus?.Invoke($"🃏 Jester Detected: {inst.DisplayName}");
                }
                // Fallback: General jester patterns
                else if (line.Contains("[Merchant]:", StringComparison.OrdinalIgnoreCase) && 
                         line.Contains("Jester", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("has arrived", StringComparison.OrdinalIgnoreCase))
                {
                    inst.JesterDetected = true;
                    inst.JesterDetectedAt = DateTime.Now;
                    OnJesterDetected?.Invoke(inst);
                    OnStatus?.Invoke($"🃏 Jester Detected (Fallback): {inst.DisplayName}");
                }
                // Regex fallback
                else if (JesterPatterns.Any(p => p.IsMatch(line)))
                {
                    inst.JesterDetected = true;
                    inst.JesterDetectedAt = DateTime.Now;
                    OnJesterDetected?.Invoke(inst);
                    OnStatus?.Invoke($"🃏 Jester Detected (Regex): {inst.DisplayName}");
                }
            }

            // Eden
            if (!inst.EdenDetected)
            {
                if (EdenPattern.IsMatch(line))
                {
                    inst.EdenDetected = true;
                    inst.EdenDetectedAt = DateTime.Now;
                    OnEdenDetected?.Invoke(inst);
                    OnStatus?.Invoke($"Eden Detected: {inst.DisplayName}");
                }
            }
        }
        
        inst.LastLogEventTime = maxNewTime;
    }

    private void EnsureLogFileAssigned(InstanceInfo inst)
    {
        if (string.IsNullOrEmpty(inst.RobloxLogFile) && inst.StartTime != default)
        {
            var robloxLogsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox", "logs"
            );
            
            if (Directory.Exists(robloxLogsDir))
            {
                var match = Directory.GetFiles(robloxLogsDir, "*.log")
                    .Select(f => new FileInfo(f))
                    .Where(f => Math.Abs((f.CreationTime - inst.StartTime).TotalSeconds) <= 120)
                    .OrderBy(f => Math.Abs((f.CreationTime - inst.StartTime).TotalSeconds))
                    .FirstOrDefault();
                
                if (match != null)
                {
                    inst.RobloxLogFile = match.FullName;
                }
            }
        }
    }

    private List<string> ReadLogLinesSafe(string logPath, int maxLines)
    {
        var result = new List<string>();
        string tempPath = Path.Combine(Path.GetTempPath(), $"bm_log_{Guid.NewGuid()}.tmp");

        try
        {
            // Copy with shared read access to handle locked files
            using (var src = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                src.CopyTo(dest);
            }

            // Read lines (Reverse is not built-in for streams, so we read all and reverse list)
            // For 2000 lines, reading all text is fine unless file is massive.
            // But Roblox files can be 100MB+. We should read from end.
            // Simplified approach: Read all lines if file is small (<5MB), else assume tail.
            // Actually, SolsScope copies *entire file* and reads all.
            // Optimization: We will just read the file using File.ReadLines().Reverse().Take(maxLines)
            // It streams, but has to scan newlines.
            
            result = File.ReadLines(tempPath).Reverse().Take(maxLines).ToList();
        }
        catch (Exception)
        {
            // Fail silently or log? SolsScope throttles errors.
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return result;
    }
    
    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}

public class InstanceInfo
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime StartTime { get; set; }
    
    public string? LogFile { get; set; }
    public long LogPosition { get; set; }
    
    // Separate Roblox log file for biome detection (BloxstrapRPC is only in Roblox logs)
    public string? RobloxLogFile { get; set; }
    public long RobloxLogPosition { get; set; }
    
    public string CurrentBiome { get; set; } = "Normal";
    public Models.BiomeType BiomeType { get; set; } = Models.BiomeType.Normal;
    public DateTime BiomeDetectedAt { get; set; }
    public string? CurrentAura { get; set; }
    
    public string? Username { get; set; }
    public string? AvatarUrl { get; set; }
    
    // Merchant tracking
    public bool MerchantDetected { get; set; }
    public DateTime MerchantDetectedAt { get; set; }
    
    // Jester tracking
    public bool JesterDetected { get; set; }
    public DateTime JesterDetectedAt { get; set; }
    
    // Eden tracking
    public bool EdenDetected { get; set; }
    public DateTime EdenDetectedAt { get; set; }
    
    // Log tracking for events
    public DateTime LastLogEventTime { get; set; } = DateTime.MinValue;
}
