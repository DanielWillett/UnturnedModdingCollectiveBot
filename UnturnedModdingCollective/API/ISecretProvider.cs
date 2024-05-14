namespace UnturnedModdingCollective.API;

/// <summary>
/// Allows swapping out for something like azure secret value, etc in the future.
/// </summary>
public interface ISecretProvider
{
    ValueTask<string?> GetSecret(string secretKey);
}
