using System;
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class CandidateUsageRecorder : ICandidateUsageRecorder
{
    public static CandidateUsageRecorder Instance { get; } = new();

    private readonly Dictionary<(SqlCompletionKind kind, string label), DateTime> _lastUsed = new();

    public CandidateUsageRecorder() { }

    public void CandidateUsed(SqlCompletionKind kind, string label)
    {
        var key = (kind, label);
        _lastUsed[key] = DateTime.UtcNow;
    }

    public bool TryGetLastUsedTime(SqlCompletionKind kind, string label, out DateTime lastUsed)
    {
        return _lastUsed.TryGetValue((kind, label), out lastUsed);
    }
}
