using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BiomeMacro.Models;

namespace BiomeMacro.Services;

public class BiomeDetectionEvent
{
    public DateTime Timestamp { get; set; }
    public string BiomeName { get; set; } = "";
    public double RarityMultiplier { get; set; }
    public int Rarity { get; set; }
    public int Pid { get; set; }
}

public class LocalStatistics
{
    public Dictionary<string, int> BiomeCounts { get; set; } = new();
    public List<BiomeDetectionEvent> History { get; set; } = new();

    // Config: Max history items to keep file size reasonable
    public int MaxHistoryItems { get; set; } = 1000;
}

public class StatisticsService
{
    private readonly string _statsPath;
    private readonly MultiInstanceManager _instanceManager;
    private LocalStatistics _stats;
    private readonly object _lock = new(); // For thread safety

    public event Action? OnStatsUpdated;

    // Expose data for UI
    public IReadOnlyDictionary<string, int> BiomeCounts
    {
        get
        {
            lock (_lock) return new Dictionary<string, int>(_stats.BiomeCounts);
        }
    }

    public IReadOnlyList<BiomeDetectionEvent> History
    {
        get
        {
            lock (_lock) return _stats.History.ToList();
        }
    }

    public StatisticsService(MultiInstanceManager instanceManager)
    {
        _instanceManager = instanceManager;
        _statsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiomeMacro", "stats.json");

        LoadStats();

        // Subscribe to biome changes
        _instanceManager.OnBiomeChanged += HandleBiomeChange;
    }

    private void HandleBiomeChange(InstanceInfo inst)
    {
        if (string.IsNullOrEmpty(inst.CurrentBiome) || inst.CurrentBiome == "Normal" || inst.CurrentBiome == "Unknown")
            return;

        // Get metadata for multiplier
        var metadata = BiomeDatabase.GetMetadata(inst.BiomeType);
        // Only track "interesting" things or if user wants everything. 
        // For now, track everything > 1x multiplier or if it's special
        // But for charts, we probably want everything except maybe pure 1x noise if it's frequent.
        // Actually, users like seeing counts of everything.

        lock (_lock)
        {
            // Update Count
            if (!_stats.BiomeCounts.ContainsKey(inst.CurrentBiome))
                _stats.BiomeCounts[inst.CurrentBiome] = 0;
            _stats.BiomeCounts[inst.CurrentBiome]++;

            // Add History Event
            _stats.History.Add(new BiomeDetectionEvent
            {
                Timestamp = DateTime.Now,
                BiomeName = inst.CurrentBiome,
                RarityMultiplier = metadata.Multiplier,
                Rarity = metadata.Rarity,
                Pid = inst.Pid
            });

            // Trim History
            if (_stats.History.Count > _stats.MaxHistoryItems)
            {
                // Remove oldest
                _stats.History.RemoveRange(0, _stats.History.Count - _stats.MaxHistoryItems);
            }
        }

        SaveStats();
        OnStatsUpdated?.Invoke();
    }

    private void LoadStats()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_statsPath))
                {
                    var json = File.ReadAllText(_statsPath);
                    _stats = JsonSerializer.Deserialize<LocalStatistics>(json) ?? new LocalStatistics();
                }
                else
                {
                    _stats = new LocalStatistics();
                }
            }
            catch
            {
                _stats = new LocalStatistics();
            }
        }
    }

    private void SaveStats()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_statsPath);
                if (dir != null) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_stats, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statsPath, json);
            }
            catch (Exception)
            {
                // Silently fail on save for now, or log it if we had a logger
            }
        }
    }
}
