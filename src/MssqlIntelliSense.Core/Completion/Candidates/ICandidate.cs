using System;
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ICandidate : IEquatable<ICandidate>
{
    string Name { get; }
    string RawName { get; }
    bool HasOwner { get; }
    ICandidate? OwnerCandidate { get; }
    string? OwnerCandidateName { get; }
    string CompletionName { get; }
    string CompletionText { get; }
    SqlObjectType ObjectType { get; }
    ICandidateCollection Children { get; }

    bool IsPartialNameMatch(string filter);
    bool IsPrefixNameMatch(string filter);
    bool IsWordNameMatch(string filter);
}
