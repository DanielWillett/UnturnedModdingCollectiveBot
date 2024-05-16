using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

[Table("review_request_roles")]
[PrimaryKey(nameof(RequestId), nameof(RoleId))]
public class ReviewRequestRole
{
    /// <summary>
    /// Store the role ID instead of foreign key in case roles are added/removed.
    /// </summary>
    public ulong RoleId { get; set; }

    [Required]
    [Column(nameof(Request))]
    [ForeignKey(nameof(Request))]
    public int RequestId { get; set; }

    [Required]
    public ReviewRequest? Request { get; set; }
    public bool ClosedUnderError { get; set; } = false;
    public ulong? PollMessageId { get; set; }
    public bool? Accepted { get; set; }
    public DateTime? UtcRoleApplied { get; set; }
    public IList<ReviewRequestVote> Votes { get; set; } = new List<ReviewRequestVote>(0);
}