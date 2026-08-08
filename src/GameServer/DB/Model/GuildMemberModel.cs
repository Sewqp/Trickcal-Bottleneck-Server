namespace GameServer.DB.Model;

public sealed class GuildMemberModel
{
    public long GuildId { get; set; }
    public long PlayerId { get; set; }
    public byte Role { get; set; } // 0=일반 1=부길드장 2=길드장
    public DateTime JoinedAt { get; set; }
}
