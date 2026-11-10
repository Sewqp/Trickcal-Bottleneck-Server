using GameServer.Checks;

try
{
    UnitChecks.Run();
    if (args.Contains("--integration")) await IntegrationChecks.RunAsync();
    Console.WriteLine($"PASS: {Check.Count} assertions");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

namespace GameServer.Checks
{
    internal static class Check
    {
        public static int Count { get; private set; }
        public static void That(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            Count++;
        }
        public static async Task ThrowsAsync(Func<Task> action, string description)
        {
            try { await action(); }
            catch { Count++; return; }
            throw new InvalidOperationException("FAIL: expected exception: " + description);
        }
    }
}
