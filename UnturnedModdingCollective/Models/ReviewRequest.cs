using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

#nullable disable

[Table("ReviewRequests")]
public class ReviewRequest
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public ulong UserId { get; set; }
    public ulong Steam64 { get; set; }
    public ulong MessageId { get; set; }
    public ulong ThreadId { get; set; }

    [StringLength(32)]
    public string GlobalName { get; set; }

    [StringLength(32)]
    public string UserName { get; set; }

    public DateTime UtcTimeStarted { get; set; }
    public DateTime? UtcTimeSubmitted { get; set; }
    public DateTime? UtcTimeClosed { get; set; }
    public bool? Accepted { get; set; }

    /// <summary>
    /// Not null if another review can be requested, which was approved by whatever user's ID is in this field.
    /// </summary>
    public ulong? ResubmitApprover { get; set; }
}