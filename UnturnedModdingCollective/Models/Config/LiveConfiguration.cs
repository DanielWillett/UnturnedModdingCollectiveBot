using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Models.Config;
public class LiveConfiguration : IDefaultable
{
    public ulong CouncilRole { get; set; }
    public TimeSpan VoteTime { get; set; }
    public TimeSpan PingTimeBeforeVoteClose { get; set; }
    public void SetDefaults()
    {
        VoteTime = TimeSpan.FromDays(3d);
        PingTimeBeforeVoteClose = TimeSpan.FromDays(1d);
    }
}