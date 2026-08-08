using Orleans;

namespace GameServer.Grains;

public sealed class OrleansClient
{
    public static readonly OrleansClient Instance = new();

    private IGrainFactory? _factory;

    private OrleansClient() { }

    public void Init(IGrainFactory factory) => _factory = factory;

    public IGrainFactory Factory => _factory ?? throw new InvalidOperationException("OrleansClient is not initialized.");
}
