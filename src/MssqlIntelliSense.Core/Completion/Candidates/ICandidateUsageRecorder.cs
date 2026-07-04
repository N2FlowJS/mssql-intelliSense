using System;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public interface ICandidateUsageRecorder
{
    void CandidateUsed(SqlCompletionKind kind, string label);
    bool TryGetLastUsedTime(SqlCompletionKind kind, string label, out DateTime lastUsed);
}
