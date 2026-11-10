using GameServer.DB;
using GameServer.DB.Repository;
using GameServer.Grains;

namespace GameServer.Progress;

public static class TheaterRecovery
{
    public static async Task<bool> RestoreAsync(long playerId, TheaterSnapshot snapshot)
    {
        var result = await RedisClient.Instance.Db.ScriptEvaluateAsync("""
            local different = redis.call('GET', KEYS[1]) ~= ARGV[1]
              or redis.call('HGET', KEYS[2], 'gold') ~= ARGV[2]
              or redis.call('HGET', KEYS[2], 'elleaf') ~= ARGV[3]
              or redis.call('HGET', KEYS[2], 'macaron') ~= ARGV[4]
            if not different then return 0 end
            redis.call('SET', KEYS[1], ARGV[1])
            redis.call('HMSET', KEYS[2], 'gold', ARGV[2], 'elleaf', ARGV[3], 'macaron', ARGV[4])
            redis.call('SADD', KEYS[3], ARGV[5])
            return 1
            """, [PlayerStatRepository.GetRedisKey(playerId), CurrencyRepository.GetRedisKey(playerId),
                ProgressRepository.DirtyPlayers],
            [snapshot.CombatPower, snapshot.Gold, snapshot.Elleaf, snapshot.Macaron, playerId]);
        return (long)result == 1;
    }
}
