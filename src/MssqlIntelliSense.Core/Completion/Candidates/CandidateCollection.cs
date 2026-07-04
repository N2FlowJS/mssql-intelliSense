using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public class CandidateCollection<T> : ICandidateCollection<T>, IEnumerable<T>, ICandidateCollection where T : class, ICandidate
{
    private readonly Dictionary<string, T> _dictionary;
    private readonly List<T> _list = new();

    public CandidateCollection(bool isCaseSensitive = false)
    {
        _dictionary = new Dictionary<string, T>(
            isCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    public CandidateCollection(IEnumerable<T> candidates, bool isCaseSensitive = false) : this(isCaseSensitive)
    {
        AddRange(candidates);
    }

    public int Count => _list.Count;

    public IEnumerable<ICandidate> Filter(string? filter, SqlObjectType type)
    {
        IEnumerable<T> source = _list;
        if (type != SqlObjectType.All)
            source = source.Where(c => c.ObjectType == type);
        if (string.IsNullOrEmpty(filter))
            return source.Cast<ICandidate>();
        return source.Where(c => c.IsPartialNameMatch(filter)).Cast<ICandidate>();
    }

    public IEnumerable<ICandidate> AllCandidates() => _list.Cast<ICandidate>();

    public void Add(ICandidate candidate)
    {
        if (candidate is not T t) return;
        _list.Add(t);
        _dictionary[candidate.Name] = t;
    }

    public void AddRange(IEnumerable<ICandidate> candidates)
    {
        foreach (var c in candidates) Add(c);
    }

    public void AddRange(IEnumerable<T> candidates)
    {
        foreach (var c in candidates) Add(c);
    }

    ICandidate? ICandidateCollection.Find(string name)
    {
        _dictionary.TryGetValue(name, out var t);
        return t;
    }

    public ICandidate? Find(string name, SqlObjectType type)
    {
        var t = this[name];
        if (t != null && t.ObjectType == type) return t;
        return null;
    }

    public T? this[string key]
    {
        get
        {
            _dictionary.TryGetValue(key, out var t);
            return t;
        }
    }

    public void Order()
    {
        _list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCulture));
    }

    public void Remove(ICandidate candidate)
    {
        if (_dictionary.TryGetValue(candidate.Name, out var t))
        {
            _list.Remove(t);
            _dictionary.Remove(t.Name);
        }
    }

    public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
}
