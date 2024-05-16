using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace UnturnedModdingCollective.Models;

[Table("persisting_roles")]
public class PersistingRole
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public ulong UserId { get; set; }
    public DateTime? UtcRemoveAt { get; set; }
    public ulong GuildId { get; set; }
    public ulong RoleId { get; set; }
    public ulong UserAddedBy { get; set; }


    public bool IsExpired(TimeProvider timeProvider) => UtcRemoveAt.HasValue && timeProvider.GetUtcNow().UtcDateTime >= UtcRemoveAt.Value;
}
