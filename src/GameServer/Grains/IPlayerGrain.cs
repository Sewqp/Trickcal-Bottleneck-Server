using Orleans;
namespace GameServer.Grains;
public interface IPlayerGrain : IGrainWithIntegerKey
{
    Task SendMessageAsync(byte[] packet);
    Task<bool> SendMatchResultAsync(Guid sessionId, byte[] packet, string? requestTraceParent);
    Task SubscribeAsync(Guid sessionId, IPlayerSessionObserver observer);
    Task<bool> IsCurrentSessionAsync(Guid sessionId);
    Task OnDisconnectAsync(Guid sessionId);
    Task<bool> IsLockedAsync();
    Task<TimeSpan?> GetLockIdleDurationAsync();
    Task<byte[]> ExecuteProgressAsync(Guid sessionId, ushort packetId, byte[] payload);
    Task FlushAsync();
}
