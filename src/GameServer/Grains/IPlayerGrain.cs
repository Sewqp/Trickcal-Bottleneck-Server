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

    // 이미 잠겨있으면 false(중복 진입/중복 요청 방지). 해제는 기본적으로 타임아웃이 아니라
    // 처리 완료 신호(가챠 완료/스토리 다 봄/서버 강제종료/접속 종료) 수신 시. 단, 그 신호가 영영
    // 안 오는 경우를 위한 하드 타임아웃 안전장치는 TouchAsync/GetLockIdleDurationAsync로 별도 처리.
    Task<bool> LockAsync();
    Task UnlockAsync();
    Task<bool> IsLockedAsync();

    // 극장 스토리 재생 중 클라가 터치(대사 넘기기)할 때마다 호출 — 락이 걸려있을 때만 의미 있음.
    // 오프라인(클라 로컬) 데이터로 대사를 넘기는 구조라 서버로 오는 유일한 생존 신호가 이것뿐이라,
    // 정상적으로 관람 중인 플레이어를 하드 타임아웃으로 잘못 끊지 않기 위한 기준점으로 씀.
    Task TouchAsync();

    // 마지막 터치(또는 락을 건 시점, 터치가 한 번도 없었으면) 이후 경과 시간. 락이 안 걸려있으면 null.
    Task<TimeSpan?> GetLockIdleDurationAsync();

    // 극장(스토리) 전용 — 진입 시점 스냅샷 저장/조회
    Task SaveSnapshotAsync(TheaterSnapshot snapshot);
    Task<TheaterSnapshot?> GetSnapshotAsync();
}
