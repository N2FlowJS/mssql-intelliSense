namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ISchemaCandidate : IDbObject
{
    IDbObject? DatabaseOwner { get; }
    ICandidateCollection Candidates { get; }
}
