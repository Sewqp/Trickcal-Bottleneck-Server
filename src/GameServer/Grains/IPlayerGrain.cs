using Orleans;

namespace GameServer.Grains;

// Game-Server-CSharp의 IPlayerGrain에서 상태(HP/MP 등 PlayerState) 부분은 뺌 — 새 데이터 모델엔
// 그 대응 개념이 없음(캐릭터별 상태는 DB.Repository.CharacterRepository, 전투력/재화는
// PlayerStatRepository/CurrencyRepository가 이미 담당). 이 그레인은 플레이어에게 서버→클라
// 메시지를 푸시하는 메시징 허브 역할 + 가챠/극장이 공유하는 세션 락 상태를 담당함.
// 락을 그레인에 두는 이유: Orleans를 쓰는 이상 분산 환경(여러 gameserver 인스턴스)에서도
// 단일 진실 소스가 되는 건 ClientSession(서버 인스턴스 로컬)이 아니라 그레인이기 때문.
public interface IPlayerGrain : IGrainWithIntegerKey
{
    Task SendMessageAsync(byte[] packet);
    Task SubscribeAsync(IPlayerSessionObserver observer);
    Task UnsubscribeAsync();
    Task OnDisconnectAsync();

    // 이미 잠겨있으면 false(중복 진입/중복 요청 방지). 해제는 타임아웃이 아니라
    // 처리 완료 신호(가챠 완료/스토리 다 봄/서버 강제종료) 수신 시에만.
    Task<bool> LockAsync();
    Task UnlockAsync();
    Task<bool> IsLockedAsync();

    // 극장(스토리) 전용 — 진입 시점 스냅샷 저장/조회
    Task SaveSnapshotAsync(TheaterSnapshot snapshot);
    Task<TheaterSnapshot?> GetSnapshotAsync();
}
