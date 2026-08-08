using Orleans;

namespace GameServer.Grains;

// 옛 ChannelGrain과 비슷한 구조(그레인이 멤버 목록 보유)지만 용도는 채팅이 아니라
// 접속/접속해제 상태 알림 전용
public interface IGuildGrain : IGrainWithIntegerKey
{
    Task MemberOnlineAsync(long playerId);
    Task MemberOfflineAsync(long playerId);
    Task<List<long>> GetOnlineMembersAsync();
}
