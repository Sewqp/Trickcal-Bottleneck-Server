using System.Diagnostics;
using ChaosClient;

var options = ChaosOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (options.DurationSeconds > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));

Console.WriteLine($"[ChaosClient] target={options.Host}:{options.Port} count={options.TotalCount} " +
    $"burst=[{options.BurstMin}-{options.BurstMax}] idleMs=[{options.IdleMinMs}-{options.IdleMaxMs}] " +
    $"heartbeatMs=[{options.HeartbeatMinMs}-{options.HeartbeatMaxMs}] churnRatio={options.ChurnRatio} " +
    $"churnMs=[{options.ChurnMinMs}-{options.ChurnMaxMs}] durationSeconds={options.DurationSeconds}");

var reporterTask = ReportLoopAsync(cts.Token);

// DummyClient의 --rampUp(일정 속도)과 대비되는 부분 — 랜덤 휴지기 뒤에 랜덤 크기의 버스트로 한꺼번에
// 몰아서 접속시킨다. TcpServer의 accept backlog, HeartbeatManager의 틱당 스캔, SessionManager의 동시
// 등록/해제 같은 "순간적으로 몰릴 때" 문제를 재현하기 위함.
var connectionTasks = new List<Task>(options.TotalCount);
int spawned = 0;
var rand = Random.Shared;
while (spawned < options.TotalCount && !cts.IsCancellationRequested)
{
    int idleMs = rand.Next(options.IdleMinMs, options.IdleMaxMs);
    try { await Task.Delay(idleMs, cts.Token); }
    catch (OperationCanceledException) { break; }

    int burst = Math.Min(rand.Next(options.BurstMin, options.BurstMax), options.TotalCount - spawned);
    for (int i = 0; i < burst; i++)
    {
        connectionTasks.Add(ChaosPlayer.RunAsync(spawned, options, cts.Token));
        spawned++;
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
    var s = ChaosStats.Snapshot();
    Console.WriteLine($"[{label} {elapsed:mm\\:ss}] connected={s.Connected} connectFailed={s.ConnectFailed} " +
        $"loginOk={s.LoginSuccess} loginFailed={s.LoginFailed} disconnected={s.Disconnected} " +
        $"hbSent={s.HeartbeatSent} hbAck={s.HeartbeatAckReceived} reconnectCycles={s.ReconnectCycles}");
}
