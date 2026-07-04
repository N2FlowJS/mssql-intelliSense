namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ILoadableCandidate
{
    string FriendlyName { get; }
    string QualifiedName { get; }
    bool IsLoaded { get; }
    bool IsLoading { get; }
    void Load();
    void Unload();
    void CancelLoad();
}
