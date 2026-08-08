using GameServer.DB.Repository;

namespace GameServer.DB;

// design-plan-draft.md 5-1절 "세션 1개 flush" 함수 — 이 함수 하나만 두고 4가지 트리거
// (하트비트 타임아웃/클라 강제종료/정상 로그아웃/서버 강제종료)가 전부 이걸 재사용함.
// 별도의 "전체 flush" 함수는 안 만듦 — 서버 종료 시엔 SessionManager를 순회하며 이 함수를 반복 호출.
public static class SessionFlush
{
    public static Task FlushOneAsync(long playerId) => Task.WhenAll(
        PlayerStatRepository.Instance.FlushToDbAsync(playerId),
        CurrencyRepository.Instance.FlushToDbAsync(playerId),
        BoardRepository.Instance.FlushToDbAsync(playerId),
        CharacterRepository.Instance.FlushLevelsToDbAsync(playerId));
}
