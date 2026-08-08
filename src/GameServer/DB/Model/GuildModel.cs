namespace GameServer.DB.Model;

public sealed class GuildModel
{
    public long GuildId { get; set; }
    public string GuildName { get; set; } = "";
    public byte GuildStatus { get; set; } // 0=정상 1=해산
    public DateTime CreatedAt { get; set; }
}
