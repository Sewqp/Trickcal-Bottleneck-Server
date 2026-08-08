using System.Diagnostics;
using DummyClient;

var options = StressOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (options.DurationSeconds > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));

Console.WriteLine($"[DummyClient] target={options.Host}:{options.Port} count={options.ConnectionCount} " +
    $"rampUpPerSecond={options.RampUpPerSecond} heartbeatIntervalSeconds={options.HeartbeatIntervalSeconds} " +
    $"durationSeconds={options.DurationSeconds}");

var reporterTask = ReportLoopAsync(cts.Token);

var connectionTasks = new List<Task>(options.ConnectionCount);
for (int i = 0; i < options.ConnectionCount; i++)
{
    if (cts.IsCancellationRequested) break;

    connectionTasks.Add(DummyPlayer.RunAsync(i, options, cts.Token));

    if ((i + 1) % options.RampUpPerSecond == 0)
    {
        try { await Task.Delay(1000, cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}

await Task.WhenAll(connectionTasks);
cts.Cancel();
await reporterTask;

PrintSnapshot(TimeSpan.Zero, "SUMMARY");
return;

async Task ReportLoopAsync(CancellationToken ct)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            PrintSnapshot(stopwatch.Elapsed, "PROGRESS");
        }
    }
    catch (OperationCanceledException)
    {
    }
}

void PrintSnapshot(TimeSpan elapsed, string label)
{
    var s = StressStats.Snapshot();
    Console.WriteLine($"[{label} {elapsed:mm\\:ss}] connected={s.Connected} connectFailed={s.ConnectFailed} " +
        $"loginOk={s.LoginSuccess} loginFailed={s.LoginFailed} disconnected={s.Disconnected} " +
        $"hbSent={s.HeartbeatSent} hbAck={s.HeartbeatAckReceived}");
}
