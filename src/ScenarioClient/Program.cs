using System.Diagnostics;
using ScenarioClient;

var options = ScenarioOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

Console.WriteLine($"[ScenarioClient] target={options.Host}:{options.Port} players={options.PlayerCount} " +
    $"rampUpPerSecond={options.RampUpPerSecond} storyDuration={options.StoryDurationSeconds}s guildName={options.GuildName}");

var stopwatch = Stopwatch.StartNew();
var playerTasks = new List<Task>(options.PlayerCount);
for (int i = 0; i < options.PlayerCount; i++)
{
    if (ct.IsCancellationRequested) break;

    playerTasks.Add(ScenarioRunner.RunOnePlayerAsync(i, options, ct));

    if ((i + 1) % options.RampUpPerSecond == 0)
    {
        try { await Task.Delay(1000, ct); }
        catch (OperationCanceledException) { break; }
    }
}

await Task.WhenAll(playerTasks);

var s = ScenarioStats.Snapshot();
Console.WriteLine($"[SUMMARY {stopwatch.Elapsed:mm\\:ss}] loginOk={s.LoginOk} gachaOk={s.GachaOk} " +
    $"enhanceOk={s.EnhanceOk} boardUnlockOk={s.BoardUnlockOk} theaterOk={s.TheaterOk} " +
    $"theaterTampered={s.TheaterTampered} guildCreateOk={s.GuildCreateOk} failed={s.Failed}");
