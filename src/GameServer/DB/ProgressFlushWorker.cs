using GameServer.Grains;
using GameServer.Progress;

namespace GameServer.DB;

public sealed class ProgressFlushWorker(CancellationToken cancellationToken)
{
    public async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var players = await RedisClient.Instance.Db.SetMembersAsync(ProgressRepository.DirtyPlayers);
                await Parallel.ForEachAsync(players, new ParallelOptions
                    { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (player, _) =>
                {
                    try { await OrleansClient.Instance.Factory.GetGrain<IPlayerGrain>((long)player).FlushAsync(); }
                    catch (Exception error) { Console.Error.WriteLine($"[Flush] retry retained for {player}: {error.Message}"); }
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) { Console.Error.WriteLine($"[Flush] scan failed: {error.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(cancellationToken)) break; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        } while (!cancellationToken.IsCancellationRequested);
    }
}
