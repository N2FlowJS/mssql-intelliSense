using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class MetadataAdapter
{
    private readonly DatabaseMetadata _metadata;
    private readonly string _currentDatabaseName;
    private readonly Lazy<ServerCandidate> _server;
    private readonly Lazy<DatabaseCandidate> _currentDatabase;
    private readonly Lazy<CandidateCollection<SchemaCandidate>> _schemas;

    public MetadataAdapter(DatabaseMetadata metadata, string currentDatabaseName = "default")
    {
        _metadata = metadata;
        _currentDatabaseName = currentDatabaseName;
        _server = new Lazy<ServerCandidate>(() => new ServerCandidate("(local)", _metadata, _currentDatabaseName));
        _currentDatabase = new Lazy<DatabaseCandidate>(() => _server.Value.CurrentDatabase ?? throw new InvalidOperationException("No current database"));
        _schemas = new Lazy<CandidateCollection<SchemaCandidate>>(() => _currentDatabase.Value.Schemas);
    }

    /// <summary>Top-level server (4-part naming root).</summary>
    public ServerCandidate Server => _server.Value;

    /// <summary>The current database with full metadata.</summary>
    public DatabaseCandidate CurrentDatabase => _currentDatabase.Value;

    /// <summary>All schemas in the current database.</summary>
    public CandidateCollection<SchemaCandidate> Schemas => _schemas.Value;

    /// <summary>Get object candidates in a specific schema.</summary>
    public CandidateCollection<ICandidate> GetCandidatesInSchema(string schemaName) =>
        _currentDatabase.Value.GetCandidatesInSchema(schemaName);

    /// <summary>All candidates across all schemas in the current database.</summary>
    public IEnumerable<ICandidate> GetAllCandidates(SqlObjectType? type = null) =>
        _currentDatabase.Value.GetAllCandidates(type);
}
