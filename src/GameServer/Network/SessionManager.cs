using System.Collections.Concurrent;

namespace GameServer.Network;

public sealed class SessionManager
{
    public static readonly SessionManager Instance = new();

    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<long, Guid> _playerIndex = new();

    private SessionManager() { }

    public void Add(ClientSession session) => _sessions[session.SessionId] = session;

    public bool Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public void RegisterPlayerId(long playerId, Guid sessionId) => _playerIndex[playerId] = sessionId;

    public void UnregisterPlayerId(long playerId)
    {
        if (playerId != 0)
            _playerIndex.TryRemove(playerId, out _);
    }

    public ClientSession? Get(Guid sessionId) => _sessions.GetValueOrDefault(sessionId);

    public ClientSession? GetByPlayerId(long playerId)
    {
        if (_playerIndex.TryGetValue(playerId, out var sessionId))
            return _sessions.GetValueOrDefault(sessionId);
        return null;
    }

    public IReadOnlyList<ClientSession> GetTimedOut(DateTime cutoff)
        => _sessions.Values.Where(s => s.LastReceivedAt < cutoff).ToList();

    // 서버 강제 종료 시(design-plan-draft.md 5절 트리거 ④) 접속 중인 세션 전부를 순회하며
    // flush하기 위한 용도
    public IReadOnlyList<ClientSession> GetAll() => _sessions.Values.ToList();

    public int Count => _sessions.Count;

    public async Task BroadcastAsync(byte[] data)
    {
        var tasks = _sessions.Values.Select(s => s.SendAsync(data));
        await Task.WhenAll(tasks);
    }
}
