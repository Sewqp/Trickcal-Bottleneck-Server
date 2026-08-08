using System.Threading.Channels;

namespace GameServer.Logging;

public enum LogLevel { Info, Error }

public sealed record LogEntry(LogLevel Level, string Message, DateTime TimestampUtc);

// Game-Server-CSharp의 AsyncLogger에서 Channel 기반 콘솔+파일 로깅 코어만 포팅.
// LLM 분석/Discord 웹훅 파이프라인은 안 가져옴 — "인프라 재사용 범위"가 아직 미정이라 미리 안 채움.
public sealed class AsyncLogger
{
    public static readonly AsyncLogger Instance = new();

    private const string LogFilePath = "logs/server.log";

    private readonly Channel<LogEntry> _channel = Channel.CreateUnbounded<LogEntry>();

    private AsyncLogger() { }

    public void Init(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        _ = RunAsync(ct);
    }

    public void LogInfo(string message) => Enqueue(LogLevel.Info, message);

    public void LogError(string message, Exception? exception = null)
        => Enqueue(LogLevel.Error, exception != null ? $"{message}\n{exception}" : message);

    private void Enqueue(LogLevel level, string message)
        => _channel.Writer.TryWrite(new LogEntry(level, message, DateTime.UtcNow));

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(ct))
                WriteToConsoleAndFile(entry);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void WriteToConsoleAndFile(LogEntry entry)
    {
        var line = $"[{entry.TimestampUtc:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}";
        Console.WriteLine(line);
        File.AppendAllText(LogFilePath, line + Environment.NewLine);
    }
}
