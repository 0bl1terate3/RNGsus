using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Defaults;
using SkiaSharp;
using BiomeMacro.Services;
using BiomeMacro.Models;

namespace BiomeMacro.UI.ViewModels;

public class GraphsViewModel : INotifyPropertyChanged
{
    private readonly StatisticsService _statsService;

    public ObservableCollection<ISeries> BiomeDistributionSeries { get; set; } = new();
    public ObservableCollection<Axis> BiomeDistributionXAxes { get; set; } = new();
    public ObservableCollection<Axis> BiomeDistributionYAxes { get; set; } = new();

    public ObservableCollection<ISeries> RarityTimelineSeries { get; set; } = new();
    public ObservableCollection<Axis> RarityTimelineXAxes { get; set; } = new();
    public ObservableCollection<Axis> RarityTimelineYAxes { get; set; } = new();

    public GraphsViewModel(StatisticsService statsService)
    {
        _statsService = statsService;
        
        InitializeCharts();
        _statsService.OnStatsUpdated += UpdateCharts;
        UpdateCharts(); // Initial load
    }

    private void InitializeCharts()
    {
        // Bar Chart Setup
        BiomeDistributionYAxes.Add(new Axis 
        { 
            Name = "Encounters",
            MinStep = 1,
            LabelsPaint = new SolidColorPaint(SKColors.Gray) 
        });

        // Line Chart Setup
        RarityTimelineXAxes.Add(new DateTimeAxis(TimeSpan.FromMinutes(10), date => date.ToString("HH:mm"))
        {
            Name = "Time",
            LabelsPaint = new SolidColorPaint(SKColors.Gray)
        });
        
        RarityTimelineYAxes.Add(new Axis
        {
            Name = "Biome Rarity (0-10)",
            LabelsPaint = new SolidColorPaint(SKColors.Gray),
            SeparatorsPaint = new SolidColorPaint(SKColors.DarkSlateGray.WithAlpha(50))
        });
        
        // Initialize simple line series
        RarityTimelineSeries.Add(new StepLineSeries<DateTimePoint>
        {
            Values = new ObservableCollection<DateTimePoint>(),
            Name = "Biome Rarity",
            Stroke = new SolidColorPaint(SKColors.Cyan) { StrokeThickness = 2 },
            Fill = null,
            GeometrySize = 0
            // TooltipLabelFormatter = (point) => $"{point.PrimaryValue}x Multiplier"
        });
    }

    private void UpdateCharts()
    {
        // Must run on UI thread if calling from service event
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateBarChart();
            UpdateLineChart();
        });
    }

    private void UpdateBarChart()
    {
        var counts = _statsService.BiomeCounts;
        
        // We want a column series for EACH biome type to have different colors
        // Or one series with different points. LiveCharts2 ColumnSeries usually takes a list of values.
        // Better: One ColumnSeries per BiomeType so we can color them easily? 
        // Or just one series "Count" and use mappers for colors. 
        // Let's do one series per biome for easy legend and auto-coloring, 
        // BUT if there are many biomes, the legend gets huge.
        // Let's do one "Counts" series and set X-Axis labels.
        
        var sortedStats = counts.OrderByDescending(x => x.Value).ToList();
        var labels = sortedStats.Select(x => x.Key).ToList();
        var values = sortedStats.Select(x => x.Value).Cast<int>().ToList(); // LiveCharts needs precise types sometimes
        
        // Re-construct XAxes if labels changed size/order significantly? 
        // Actually LiveCharts2 is reactive.
        if (BiomeDistributionXAxes.Count == 0) 
        {
            BiomeDistributionXAxes.Add(new Axis 
            { 
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                MinStep = 1,
                ForceStepToMin = true
            });
        }
        BiomeDistributionXAxes[0].Labels = labels;

        if (BiomeDistributionSeries.Count == 0)
        {
            BiomeDistributionSeries.Add(new ColumnSeries<int>
            {
                Values = new ObservableCollection<int>(values),
                Name = "Encounters",
                DataLabelsSize = 12,
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top,
                Stroke = null,
                MaxBarWidth = 50
                // Color mapping could be added here if needed
            });
        }
        else
        {
            // Update existing series values safely
            var series = (ColumnSeries<int>)BiomeDistributionSeries[0];
            // If the count of items changed, we might need to reset the collection to correspond with labels
            if (series.Values is ObservableCollection<int> obsValues)
            {
                obsValues.Clear();
                foreach (var v in values) obsValues.Add(v);
            }
        }
    }

    private void UpdateLineChart()
    {
        var history = _statsService.History;
        if (history.Count == 0) return;

        // Filter last 24h? Or just show all available history in memory
        var points = history
            .OrderBy(x => x.Timestamp)
            .Select(x => new DateTimePoint(x.Timestamp, x.Rarity)) // Track Rarity
            .ToList();

        if (RarityTimelineSeries[0].Values is ObservableCollection<DateTimePoint> obsValues)
        {
            // Optimization: Only add new points if appending? 
            // For now, simple clear/add for correctness
            obsValues.Clear();
            foreach (var p in points) obsValues.Add(p);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
