using System.Diagnostics;

namespace OpticalBackup.Core;

/// <summary>A progress update for a long-running operation.</summary>
public sealed record ProgressInfo(string Stage, string? Item, long Done, long Total);

/// <summary>Accumulates byte counts and forwards them to an IProgress at a limited rate.</summary>
public sealed class ProgressMeter
{
    private readonly IProgress<ProgressInfo>? _sink;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReport = -1000;
    private string _stage = "";
    private string? _item;
    private long _done, _total;

    public ProgressMeter(IProgress<ProgressInfo>? sink) { _sink = sink; }

    public void Start(string stage, long total)
    {
        _stage = stage; _total = total; _done = 0; _item = null;
        Report(force: true);
    }

    public void Item(string? item) { _item = item; Report(force: false); }

    public void Add(long delta)
    {
        _done += delta;
        Report(force: false);
    }

    public void Finish() { _done = _total; Report(force: true); }

    private void Report(bool force)
    {
        if (_sink is null) return;
        long now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastReport < 100) return;
        _lastReport = now;
        _sink.Report(new ProgressInfo(_stage, _item, _done, _total));
    }
}
