namespace GameServer.Packet;

public enum PacketId : ushort
{
    LoginRequest       = 1000,
    LoginResult        = 1001,

    TheaterEnterRequest = 2000,
    TheaterEnterResult  = 2001,
    TheaterExitRequest  = 2002,
    TheaterExitResult   = 2003,
    TheaterTouchRequest = 2004, // 응답 없음(fire-and-forget) — 생명주기 갱신 전용 신호

    MatchRequest       = 3000,
    MatchResult        = 3001,
    MatchCancelRequest = 3002, // 응답 없음(fire-and-forget) — 클라가 대기를 포기했을 때 큐에서 빼달라는 신호

    ItemAcquireRequest = 4000,
    ItemAcquireResult  = 4001,
    InventoryRequest   = 4002,
    InventoryResult    = 4003,
    ItemUseRequest     = 4004,
    ItemUseResult      = 4005,

    CharacterListRequest      = 5000,
    CharacterListResult       = 5001,
    CharacterEnhanceEnterRequest = 5002,
    CharacterEnhanceEnterResult  = 5003,
    CharacterEnhanceExitRequest  = 5004,
    CharacterEnhanceExitResult   = 5005,

    BoardListRequest   = 6000,
    BoardListResult    = 6001,
    BoardUnlockRequest = 6002,
    BoardUnlockResult  = 6003,

    GachaRequest = 7000,
    GachaResult  = 7001,

    GuildCreateRequest = 8000,
    GuildCreateResult  = 8001,
    GuildJoinRequest   = 8002,
    GuildJoinResult    = 8003,
    GuildLeaveRequest  = 8004,
    GuildLeaveResult   = 8005,
    GuildInfoRequest   = 8006,
    GuildInfoResult    = 8007,
    GuildMemberStatusNotify = 8008,

    Heartbeat          = 9000,
    ReconnectRequest   = 9001,
    ReconnectResult    = 9002,

    // 테스트 더미 전용 — 실서비스엔 없을 디버그 치트. 어드민 세션(로그인 이름이 "admin")에서만
    // 정상 동작하도록 의도했지만, DebugHandler에 그 체크가 없어 비-어드민도 재화를 받아감(의도된 버그,
    // apm+Graphify가 이 핸들러를 원인 코드로 짚어내는지 검증하기 위한 테스트 케이스).
    DebugGrantCurrencyRequest = 9100,
    DebugGrantCurrencyResult  = 9101,
}
