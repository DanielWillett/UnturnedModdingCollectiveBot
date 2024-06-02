using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Models.Config;
public class LiveConfiguration : IDefaultable
{
    /// <summary>
    /// Amount of time between when a portfolio is submitted and when the vote is closed. Rounds to the nearest hour. Max 7 days.
    /// </summary>
    public TimeSpan VoteTime { get; set; }

    /// <summary>
    /// Amount of time before the vote closes where the applying role is pinged again.
    /// </summary>
    public TimeSpan PingTimeBeforeVoteClose { get; set; }

    /// <summary>
    /// Amount of time that has to go by before someone can re-apply for the same role. Zero implies indefinite.
    /// </summary>
    public TimeSpan TimeBetweenApplications { get; set; }

    /// <summary>
    /// Number of yes votes (with no votes subtracted) needed to automatically accept an applicant before the vote is over.
    /// </summary>
    public int VoteNetAutoAccept { get; set; }

    /// <summary>
    /// Should the applicant be removed from the thread on submit?
    /// </summary>
    public bool RemoveApplicantFromThread { get; set; }
    public void SetDefaults()
    {
        TimeBetweenApplications = TimeSpan.FromDays(30d);
        VoteTime = TimeSpan.FromDays(3d);
        PingTimeBeforeVoteClose = TimeSpan.FromDays(1d);
        VoteNetAutoAccept = 5;
    }
}