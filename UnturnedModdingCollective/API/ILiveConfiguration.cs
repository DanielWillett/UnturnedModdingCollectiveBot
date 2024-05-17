namespace UnturnedModdingCollective.API;
public interface ILiveConfiguration<out T> where T : class
{
    T Configuraiton { get; }
    T Reload();
    void Save();
}
