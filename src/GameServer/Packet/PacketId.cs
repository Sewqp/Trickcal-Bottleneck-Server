namespace GameServer.Packet;

public enum PacketId : ushort
{
    LoginRequest       = 1000,
    LoginResult        = 1001,

    TheaterEnterRequest = 2000,
    TheaterEnterResult  = 2001,
    TheaterExitRequest  = 2002,
    TheaterExitResult   = 2003,

    MatchRequest       = 3000,
    MatchResult        = 3001,

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
}
