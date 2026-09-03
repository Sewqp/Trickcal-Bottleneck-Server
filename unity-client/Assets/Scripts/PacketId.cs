// GameServer.Packet.PacketId 미러 — 이 데모(로그인/캐릭터목록/가챠/강화/디버그치트)에서
// 실제로 쓰는 값만 옮겨둠. 서버 쪽 정의(src/GameServer/Packet/PacketId.cs)와 값이 어긋나면
// 이 데모도 깨지니, 서버 enum이 바뀌면 여기도 같이 맞출 것.
public enum PacketId : ushort
{
    LoginRequest = 1000,
    LoginResult = 1001,

    CharacterListRequest = 5000,
    CharacterListResult = 5001,
    CharacterEnhanceEnterRequest = 5002,
    CharacterEnhanceEnterResult = 5003,
    CharacterEnhanceExitRequest = 5004,
    CharacterEnhanceExitResult = 5005,

    GachaRequest = 7000,
    GachaResult = 7001,

    Heartbeat = 9000,

    DebugGrantCurrencyRequest = 9100,
    DebugGrantCurrencyResult = 9101,
}
