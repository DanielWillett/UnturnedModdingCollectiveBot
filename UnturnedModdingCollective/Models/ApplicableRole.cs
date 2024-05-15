using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

#nullable disable

[Table("applicable_roles")]
public class ApplicableRole
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public ulong UserAddedBy { get; set; }
    public ulong RoleId { get; set; }
    public ulong GuildId { get; set; }

    [StringLength(32)]
    public string Emoji { get; set; }

    [StringLength(100 /* max length in API */)]
    public string Description { get; set; }
}