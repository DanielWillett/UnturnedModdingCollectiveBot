using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Models.Config;
public class LiveConfiguration : IDefaultable
{
    public ulong CouncilRole { get; set; }
    public void SetDefaults()
    {

    }
}
