using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

[Table("review_request_roles")]
[PrimaryKey(nameof(RequestId), nameof(RoleId))]
public class ReviewRequestRoleLink
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
}