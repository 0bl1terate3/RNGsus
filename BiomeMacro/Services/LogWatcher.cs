using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiomeMacro.Services;

public class LogWatcher : IDisposable
{
    private readonly string _directory;
    private readonly string _filePattern;
    private readonly CancellationTokenSource _cts = new();
    
    private string? _currentFile;
    private long _lastPosition;
    private FileSystemWatcher? _watcher;
    private Task? _watchTask;
    
    public event Action<string>? OnNewLine;
    public event Action<string>? OnError;
    public event Action<string>? OnStatus;
    
    public bool IsWatching { get; private set; }
    public string CurrentFile => _currentFile ?? "None";
    
    public LogWatcher(string directory, string filePattern = "*.log")
    {
        _directory = directory;
        _filePattern = filePattern;
    }
    
    public void Start()
    {
        if (IsWatching) return;
        
        if (!Directory.Exists(_directory))
        {
            OnError?.Invoke($"Directory not found: {_directory}");
            return;
        }
        
        IsWatching = true;
        
        // Find the most recent log file
        FindLatestLogFile();
        
        // Set up file system watcher for new files
        _watcher = new FileSystemWatcher(_directory)
        {
            Filter = _filePattern,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        
        _watcher.Created += (_, e) =>
        {
            OnStatus?.Invoke($"New log file detected: {e.Name}");
            _currentFile = e.FullPath;
            _lastPosition = 0;
        };
        
        _watcher.Changed += (_, e) =>
        {
            if (e.FullPath == _currentFile)
            {
                ReadNewLines();
            }
        };
        
        // Start background polling for changes
        _watchTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    ReadNewLines();
                    await Task.Delay(500, _cts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Error reading log: {ex.Message}");
                    await Task.Delay(2000, _cts.Token);
                }
            }
        }, _cts.Token);
        
        OnStatus?.Invoke($"Started watching: {_directory}");
    }
    
    private void FindLatestLogFile()
    {
        try
        {
            var files = Directory.GetFiles(_directory, _filePattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            
            if (files.Count > 0)
            {
                _currentFile = files[0].FullName;
                _lastPosition = files[0].Length; // Start from end to avoid old entries
                OnStatus?.Invoke($"Watching: {files[0].Name}");
            }
            else
            {
                OnStatus?.Invoke("No log files found, waiting...");
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Error finding log files: {ex.Message}");
        }
    }
    
    private void ReadNewLines()
    {
        if (string.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile))
        {
            FindLatestLogFile();
            return;
        }
        
        try
        {
            using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            if (fs.Length < _lastPosition)
            {
                // File was truncated or replaced, start from beginning
                _lastPosition = 0;
            }
            
            if (fs.Length == _lastPosition)
                return;
            
            fs.Seek(_lastPosition, SeekOrigin.Begin);
            
            using var reader = new StreamReader(fs);
            
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    OnNewLine?.Invoke(line);
                }
            }
            
            _lastPosition = fs.Position;
        }
        catch (IOException)
        {
            // File might be locked, will retry on next poll
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Error reading file: {ex.Message}");
        }
    }
    
    public void Stop()
    {
        if (!IsWatching) return;
        
        IsWatching = false;
        _cts.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        
        OnStatus?.Invoke("Stopped watching");
    }
    
    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
