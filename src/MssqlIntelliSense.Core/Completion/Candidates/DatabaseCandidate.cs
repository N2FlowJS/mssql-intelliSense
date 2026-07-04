using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class DatabaseCandidate : DbObjectBase
{
    private readonly DatabaseMetadata _metadata;
    private CandidateCollection<SchemaCandidate>? _schemas;

    public DatabaseCandidate(string name, IDbObject? serverOwner, DatabaseMetadata metadata, bool isCurrent = false)
        : base(name)
    {
        _metadata = metadata;
        ServerOwner = serverOwner;
        IsCurrent = isCurrent;
        DatabaseName = name;
    }

    public IDbObject? ServerOwner { get; }
    public override IDbObject? Owner => ServerOwner;
    public bool IsCurrent { get; }
    public override SqlObjectType ObjectType => SqlObjectType.Database;

    public override string CompletionName => Name;
    public override string CompletionText => SqlCompletionHelper.Quote(Name) + ".";

    internal DatabaseMetadata Metadata => _metadata;

    public CandidateCollection<SchemaCandidate> Schemas
    {
        get
        {
            if (_schemas == null)
                _schemas = BuildSchemas();
            return _schemas;
        }
    }

    public CandidateCollection<ICandidate> GetCandidatesInSchema(string schemaName)
    {
        var schema = Schemas[schemaName];
        if (schema == null) return new CandidateCollection<ICandidate>();

        if (schema.Children is CandidateCollection<ICandidate> children)
            return children;
        return new CandidateCollection<ICandidate>();
    }

    public TableCandidate? FindTableCandidate(string? schema, string name)
    {
        if (_schemas == null)
            _schemas = BuildSchemas();
        foreach (var s in _schemas)
        {
            foreach (var c in s.Children.AllCandidates())
            {
                if (c is TableCandidate tc &&
                    tc.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    (schema is null || tc.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))
                    return tc;
            }
        }
        return null;
    }

    public IEnumerable<ICandidate> GetAllCandidates(SqlObjectType? type = null)
    {
        foreach (var schema in Schemas)
        {
            foreach (var c in GetCandidatesInSchema(schema.Name).AllCandidates())
            {
                if (type == null || type == SqlObjectType.All || c.ObjectType == type.Value)
                    yield return c;
            }
        }
    }

    private CandidateCollection<SchemaCandidate> BuildSchemas()
    {
        var allSchemaNames = _metadata.Tables.Select(t => t.Schema)
            .Concat(_metadata.Views.Select(v => v.Schema))
            .Concat(_metadata.Procedures.Select(p => p.Schema))
            .Concat(_metadata.Functions.Select(f => f.Schema))
            .Concat(_metadata.Synonyms.Select(s => s.Schema))
            .Concat(_metadata.UserTypes.Select(u => u.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // First pass: create SchemaCandidate objects with empty children
        var schemas = new CandidateCollection<SchemaCandidate>();
        foreach (var schemaName in allSchemaNames)
            schemas.Add(new SchemaCandidate(schemaName, this, new CandidateCollection<ICandidate>()));

        // Second pass: populate each schema's Children collection
        foreach (var schema in schemas)
        {
            var children = (CandidateCollection<ICandidate>)schema.Children;
            foreach (var t in _metadata.Tables.Where(t => t.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new TableCandidate(t, schema));
            foreach (var v in _metadata.Views.Where(v => v.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new ViewCandidate(v, schema));
            foreach (var p in _metadata.Procedures.Where(p => p.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new ProcedureCandidate(p, schema));
            foreach (var f in _metadata.Functions.Where(f => f.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new FunctionCandidate(f, schema));
            foreach (var s in _metadata.Synonyms.Where(s => s.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new SynonymCandidate(s, schema));
            foreach (var u in _metadata.UserTypes.Where(u => u.Schema.Equals(schema.Name, StringComparison.OrdinalIgnoreCase)))
                children.Add(new UserTypeCandidate(u, schema));
        }

        return schemas;
    }
}
