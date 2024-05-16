using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

#nullable disable

[Table("review_request_votes")]
[PrimaryKey(nameof(RequestId), nameof(RoleId), nameof(VoteIndex))]
public class ReviewRequestVote
{
    public int VoteIndex { get; set; }
    public ulong RoleId { get; set; }

    [Required]
    [Column(nameof(Request))]
    [ForeignKey(nameof(Request))]
    public int RequestId { get; set; }

    [Required]
    public ReviewRequestRole Role { get; set; }
    [Required]
    public ReviewRequest Request { get; set; }
    public bool Vote { get; set; }
    public ulong UserId { get; set; }
    
    [StringLength(32)]
    public string GlobalName { get; set; }
    
    [StringLength(32)]
    public string UserName { get; set; }
}