﻿using Microsoft.EntityFrameworkCore;
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
    public int YesVotes { get; set; }
    public int NoVotes { get; set; }
    public ulong ThreadId { get; set; }

    /// <summary>
    /// Not null if another review can be requested, which was approved by whatever user's ID is in this field.
    /// </summary>
    public ulong? ResubmitApprover { get; set; }
    public DateTime? UtcTimeCancelled { get; set; }
    public DateTime? UtcTimeSubmitted { get; set; }
    public DateTime? UtcTimeVoteExpires { get; set; }
    public DateTime? UtcTimeClosed { get; set; }
    public IList<ReviewRequestVote> Votes { get; set; } = new List<ReviewRequestVote>(0);
}