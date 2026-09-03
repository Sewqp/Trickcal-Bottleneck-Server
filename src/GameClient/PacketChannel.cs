using System.Net.Sockets;
using System.Threading.Channels;
using GameServer.Packet;

namespace GameClient;

// ScenarioClient의 PacketChannel과 같은 이유(MatchResult는 요청/응답이 아니라 순수 비동기 푸시)로
// 하나의 read loop가 들어오는 패킷을 종류별로 갈라야 함. 이 클라이언트는 그 위에 하나 더 갈라야 하는
// 이유가 있음: 메뉴 대기 중에도 30초 하트비트 타임아웃을 피하려고 백그라운드에서 계속 Heartbeat를
// 보내는데, 그 ack가 인터랙티브 요청의 ReadResponseAsync()와 같은 큐에 섞이면 사용자가 방금 고른
// 메뉴의 응답 대신 엉뚱한 하트비트 ack를 읽어버리는 경합이 생김 — 그래서 Heartbeat도 MatchResult처럼
// 완전히 별도 경로로 분리해 버린다.
public sealed class PacketChannel
{
    private readonly NetworkStream _stream;
    private readonly Channel<(PacketId Id, byte[] Payload)> _responses = Channel.CreateUnbounded<(PacketId, byte[])>();
    private readonly Channel<byte[]> _matchResults = Channel.CreateUnbounded<byte[]>();

    public PacketChannel(NetworkStream stream)
    {
        _stream = stream;
    }

    public void Start(CancellationToken ct) => _ = ReadLoopAsync(ct);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (id, payload) = await PacketReader.ReadPacketAsync(_stream, ct);
                switch (id)
                {
                    case PacketId.MatchResult:
                        await _matchResults.Writer.WriteAsync(payload, ct);
                        break;
                    case PacketId.Heartbeat:
                    case PacketId.GuildMemberStatusNotify:
                        break; // 백그라운드 생존 신호/정보용 푸시 — 대기자 없이 흘려보냄
                    default:
                        await _responses.Writer.WriteAsync((id, payload), ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 서버가 연결을 끊는 등 — 대기 중인 ReadResponseAsync/ReadMatchResultAsync가 채널 종료로
            // 예외를 받도록 정리만 하고 조용히 끝낸다.
        }
        finally
        {
            _responses.Writer.TryComplete();
            _matchResults.Writer.TryComplete();
        }
    }

    public Task SendAsync(byte[] packet, CancellationToken ct) => _stream.WriteAsync(packet, ct).AsTask();

    public async Task<(PacketId Id, byte[] Payload)> ReadResponseAsync(CancellationToken ct)
        => await _responses.Reader.ReadAsync(ct);

    public async Task<byte[]> ReadMatchResultAsync(CancellationToken ct)
        => await _matchResults.Reader.ReadAsync(ct);

    // 이전에 타임아웃으로 포기한 매칭 요청의 뒤늦은 MatchResult 푸시가 채널에 남아있을 수 있음 —
    // 그 상태로 새 매칭을 신청하면 ReadMatchResultAsync가 이 묵은 응답을 새 요청의 결과로 잘못 읽어버림
    // (서버에 취소 요청을 보내도 네트워크 왕복 사이의 경합까진 못 막으므로, 새 요청 직전에 비워버리는
    // 게 가장 확실함).
    public void DrainStaleMatchResults()
    {
        while (_matchResults.Reader.TryRead(out _)) { }
    }
}
