using System.IO;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// One tailed log file: reader + processor + engine, pumped on a background
/// task. All engine mutation happens under the manager's shared sync object,
/// which also serializes the correlator callbacks EncounterEnded fires —
/// take the same lock to read engine state from the UI.
/// </summary>
public sealed class LogSource : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private readonly object _sync;

    public string Path { get; }
    public string Owner { get; }
    public bool ParseFromStart { get; }
    public ParserEngine Engine { get; }
    public LogLineProcessor Processor { get; }
    public TriggerEngine? TriggerEngine { get; }

    /// <summary>Set when the tail loop dies (file unreadable etc.).</summary>
    public Exception? Error { get; private set; }
    public bool Completed => _task.IsCompleted;

    public LogSource(string path, bool parseFromStart, object sync, EngineOptions engineOptions, TimeSpan pollInterval, TriggerEngine? triggers = null)
    {
        Path = path;
        ParseFromStart = parseFromStart;
        _sync = sync;
        Owner = DeriveOwner(path);
        Engine = new ParserEngine(path, Owner, engineOptions);
        TriggerEngine = triggers;
        Processor = new LogLineProcessor(Engine, triggers);
        var reader = new LogTailReader(path, new LogTailOptions
        {
            StartAtEnd = !parseFromStart,
            PollInterval = pollInterval,
        });
        _task = Task.Run(() => PumpAsync(reader, _cts.Token));
    }

    /// <summary>eq2log_Charname.[…].txt → Charname.</summary>
    public static string DeriveOwner(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];

    private async Task PumpAsync(LogTailReader reader, CancellationToken ct)
    {
        try
        {
            await foreach (var tailed in reader.ReadLinesAsync(ct))
            {
                if (!LogLine.TryParse(tailed.Raw, out var line))
                    continue;
                line = line with { ObservedAt = tailed.ObservedAt };
                lock (_sync)
                {
                    Processor.Process(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Error = ex;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; nothing to report on shutdown.
        }
        _cts.Dispose();
    }
}
