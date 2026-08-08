using Orleans;

namespace GameServer.Grains;

public sealed class PlayerGrain : Grain, IPlayerGrain
{
    private IPlayerSessionObserver? _observer;
    private bool _locked;
    private TheaterSnapshot? _snapshot;

    public Task SendMessageAsync(byte[] packet) => _observer?.DeliverAsync(packet) ?? Task.CompletedTask;

    public Task SubscribeAsync(IPlayerSessionObserver observer)
    {
        _observer = observer;
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync()
    {
        _observer = null;
        return Task.CompletedTask;
    }

    public Task OnDisconnectAsync()
    {
        _observer = null;
        return Task.CompletedTask;
    }

    public Task<bool> LockAsync()
    {
        if (_locked) return Task.FromResult(false);
        _locked = true;
        return Task.FromResult(true);
    }

    public Task UnlockAsync()
    {
        _locked = false;
        _snapshot = null;
        return Task.CompletedTask;
    }

    public Task<bool> IsLockedAsync() => Task.FromResult(_locked);

    public Task SaveSnapshotAsync(TheaterSnapshot snapshot)
    {
        _snapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task<TheaterSnapshot?> GetSnapshotAsync() => Task.FromResult(_snapshot);
}
