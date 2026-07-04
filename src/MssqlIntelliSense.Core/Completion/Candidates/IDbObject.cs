using System;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface IDbObject : ICandidate, IEquatable<ICandidate>
{
    string DatabaseName { get; }
    bool HasSystemObjectFlag { get; }
    bool IsUserDefinedObject { get; }
    IDbObject? Owner { get; }
    string? Description { get; set; }
}
