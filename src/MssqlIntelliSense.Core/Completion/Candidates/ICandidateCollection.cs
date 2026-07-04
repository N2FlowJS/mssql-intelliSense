using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ICandidateCollection
{
    ICandidate? Find(string name);
    ICandidate? Find(string name, SqlObjectType type);
    IEnumerable<ICandidate> Filter(string? filter, SqlObjectType type);
    IEnumerable<ICandidate> AllCandidates();
    void Add(ICandidate candidate);
    void AddRange(IEnumerable<ICandidate> candidates);
    void Order();
    int Count { get; }
}

public interface ICandidateCollection<T> : ICandidateCollection, IEnumerable<T> where T : class, ICandidate
{
    T? this[string key] { get; }
}
