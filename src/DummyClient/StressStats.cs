namespace DummyClient;

public static class StressStats
{
    private static long _connected;
    private static long _connectFailed;
    private static long _loginSuccess;
    private static long _loginFailed;
    private static long _disconnected;
    private static long _heartbeatSent;
    private static long _heartbeatAckReceived;

    public static void IncrementConnected() => Interlocked.Increment(ref _connected);
    public static void IncrementConnectFailed() => Interlocked.Increment(ref _connectFailed);
    public static void IncrementLoginSuccess() => Interlocked.Increment(ref _loginSuccess);
    public static void IncrementLoginFailed() => Interlocked.Increment(ref _loginFailed);
    public static void IncrementDisconnected() => Interlocked.Increment(ref _disconnected);
    public static void IncrementHeartbeatSent() => Interlocked.Increment(ref _heartbeatSent);
    public static void IncrementHeartbeatAckReceived() => Interlocked.Increment(ref _heartbeatAckReceived);

    public static StressSnapshot Snapshot() => new(
        Interlocked.Read(ref _connected),
        Interlocked.Read(ref _connectFailed),
        Interlocked.Read(ref _loginSuccess),
        Interlocked.Read(ref _loginFailed),
        Interlocked.Read(ref _disconnected),
        Interlocked.Read(ref _heartbeatSent),
        Interlocked.Read(ref _heartbeatAckReceived));
}

public readonly record struct StressSnapshot(
    long Connected,
    long ConnectFailed,
    long LoginSuccess,
    long LoginFailed,
    long Disconnected,
    long HeartbeatSent,
    long HeartbeatAckReceived);
