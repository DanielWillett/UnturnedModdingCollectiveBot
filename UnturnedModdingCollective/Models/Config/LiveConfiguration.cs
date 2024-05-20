using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Models.Config;
public class LiveConfiguration : IDefaultable
{
    /// <summary>
    /// Role that gets pinged to vote on membership.
    /// </summary>
    public ulong CouncilRole { get; set; }

    /// <summary>
    /// Amount of time between when a portfolio is submitted and when the vote is closed. Rounds to the nearest hour. Max 7 days.
    /// </summary>
    public TimeSpan VoteTime { get; set; }

    /// <summary>
    /// Amount of time before the vote closes where <see cref="CouncilRole"/> is pinged.
    /// </summary>
    public TimeSpan PingTimeBeforeVoteClose { get; set; }
    public void SetDefaults()
    {
        VoteTime = TimeSpan.FromDays(3d);
        PingTimeBeforeVoteClose = TimeSpan.FromDays(1d);
    }
}