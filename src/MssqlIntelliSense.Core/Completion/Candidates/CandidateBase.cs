using System;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public abstract class CandidateBase : ICandidate, IEquatable<ICandidate>
{
    private readonly string _name;

    protected CandidateBase(string name)
    {
        _name = name;
    }

    public virtual string Name => _name;
    public virtual string RawName => Name;
    public virtual bool HasOwner => OwnerCandidate != null;
    public virtual ICandidate? OwnerCandidate => (this as IDbObject)?.Owner;
    public virtual string? OwnerCandidateName => OwnerCandidate?.Name;
    public virtual string CompletionName => Name;
    public virtual string CompletionText => Name;
    public abstract SqlObjectType ObjectType { get; }
    public virtual ICandidateCollection Children => null!;

    public virtual bool IsPartialNameMatch(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return CompletionName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               SqlCompletionHelper.Matches(CompletionName, filter);
    }

    public virtual bool IsPrefixNameMatch(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return CompletionName.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
    }

    public virtual bool IsWordNameMatch(string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return SqlCompletionHelper.Matches(CompletionName, filter);
    }

    public virtual bool Equals(ICandidate? other)
    {
        if (other is null) return false;
        return Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase) &&
               ObjectType == other.ObjectType &&
               (OwnerCandidateName?.Equals(other.OwnerCandidateName, StringComparison.OrdinalIgnoreCase) ?? other.OwnerCandidateName is null);
    }

    public override bool Equals(object? obj) => obj is ICandidate other && Equals(other);
    public override int GetHashCode() =>
        Name.ToUpperInvariant().GetHashCode() ^ ((int)ObjectType).GetHashCode() ^ (OwnerCandidateName?.ToUpperInvariant().GetHashCode() ?? 0);
    public override string ToString() => Name;
}
