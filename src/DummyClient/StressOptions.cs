namespace DummyClient;

public sealed record StressOptions(
    string Host,
    int Port,
    int ConnectionCount,
    int RampUpPerSecond,
    int HeartbeatIntervalSeconds,
    int DurationSeconds)
{
    public static StressOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = 9100;
        int connectionCount = 1000;
        int rampUpPerSecond = 200;
        int heartbeatIntervalSeconds = 10;
        int durationSeconds = 60;

        for (int i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": port = int.Parse(args[i + 1]); break;
                case "--count": connectionCount = int.Parse(args[i + 1]); break;
                case "--rampUp": rampUpPerSecond = int.Parse(args[i + 1]); break;
                case "--heartbeatInterval": heartbeatIntervalSeconds = int.Parse(args[i + 1]); break;
                case "--duration": durationSeconds = int.Parse(args[i + 1]); break;
            }
        }

        return new StressOptions(host, port, connectionCount, rampUpPerSecond, heartbeatIntervalSeconds, durationSeconds);
    }
}
