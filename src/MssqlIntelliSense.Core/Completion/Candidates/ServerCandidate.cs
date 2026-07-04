using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class ServerCandidate : DbObjectBase
{
    private readonly DatabaseMetadata _currentMetadata;
    private readonly string _currentDatabaseName;
    private CandidateCollection<DatabaseCandidate>? _databases;

    public ServerCandidate(string name, DatabaseMetadata currentMetadata, string currentDatabaseName)
        : base(name)
    {
        _currentMetadata = currentMetadata;
        _currentDatabaseName = currentDatabaseName;
        DatabaseName = "";
    }

    public override IDbObject? Owner => null;
    public override SqlObjectType ObjectType => SqlObjectType.Server;
    public override string CompletionText => SqlCompletionHelper.Quote(Name) + ".";

    public CandidateCollection<DatabaseCandidate> Databases
    {
        get
        {
            if (_databases == null)
                _databases = BuildDatabases();
            return _databases;
        }
    }

    public DatabaseCandidate? CurrentDatabase => Databases[_currentDatabaseName];

    private CandidateCollection<DatabaseCandidate> BuildDatabases()
    {
        var databases = new CandidateCollection<DatabaseCandidate>();

        // Current database with full metadata
        databases.Add(new DatabaseCandidate(_currentDatabaseName, this, _currentMetadata, isCurrent: true));

        // Other databases from metadata.Databases (name-only)
        foreach (var dbName in _currentMetadata.Databases)
        {
            if (databases[dbName] == null)
                databases.Add(new DatabaseCandidate(dbName, this, DatabaseMetadata.Empty, isCurrent: false));
        }

        return databases;
    }
}
