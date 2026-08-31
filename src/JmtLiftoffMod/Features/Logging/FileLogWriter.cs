using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace JmtLiftoffMod.Features.Logging;

/// <summary>
/// Manages asynchronous file logging via a background thread.
/// All file writes are enqueued and drained off the Unity main thread
/// to avoid frame hitches during burst logging.
/// </summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly BlockingCollection<(string path, string text)> _queue;
    private readonly Thread _writerThread;
    private bool _disposed;

    public FileLogWriter(int capacity = 1000)
    {
        _queue = new BlockingCollection<(string, string)>(capacity);
        _writerThread = new Thread(DrainLoop) { IsBackground = true, Name = "LiftoffLogWriter" };
        _writerThread.Start();
    }

    /// <summary>Enqueue a line to be written to the given file path.</summary>
    public void Write(string path, string text)
    {
        if (!_disposed)
            _queue.TryAdd((path, text));
    }

    /// <summary>Write a timestamped line to a state log file.</summary>
    public void WriteStateLine(string path, string message)
    {
        Write(path, $"[{DateTime.UtcNow:O}] {message}");
    }

    /// <summary>Write a timestamped line to a race log file.</summary>
    public void WriteRaceLine(string path, string message)
    {
        Write(path, $"[{DateTime.UtcNow:O}] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    public static void CleanOldLogs(string pluginDir, string eventCodeDir, int maxAgeDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var dir in new[] { pluginDir, eventCodeDir })
            {
                foreach (var file in Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.jsonl")))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
            }
        }
        catch { /* best effort cleanup */ }
    }

    private void DrainLoop()
    {
        foreach (var (path, text) in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(path, text + Environment.NewLine); }
            catch { /* best effort */ }
        }
    }
}
