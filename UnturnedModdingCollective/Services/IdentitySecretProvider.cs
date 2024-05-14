using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Services;
public class IdentitySecretProvider : ISecretProvider
{
    public ValueTask<string?> GetSecret(string secretKey)
    {
        return new ValueTask<string?>(secretKey);
    }
}
