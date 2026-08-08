using Orleans;

namespace GameServer.Grains;

// 극장(스토리) 진입 시점 스탯·재화 스냅샷 — 트릭컬 스토리 자체는 재생 중 변화가 없는 컨텐츠라
// 퇴장 시점에 이 값과 다르면 위변조로 간주하고 이 값으로 강제 복구함(TheaterHandler 참고)
[GenerateSerializer]
public sealed class TheaterSnapshot
{
    [Id(0)] public long CombatPower { get; set; }
    [Id(1)] public long Gold { get; set; }
    [Id(2)] public long Elleaf { get; set; }
    [Id(3)] public long Macaron { get; set; }
}
