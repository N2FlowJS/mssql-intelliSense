using System;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public abstract class DbObjectBase : CandidateBase, IDbObject
{
    protected DbObjectBase(string name) : base(name) { }

    public virtual string DatabaseName { get; protected set; } = "";
    public virtual bool HasSystemObjectFlag => false;
    public abstract IDbObject? Owner { get; }

    public string? Description { get; set; }

    public bool IsUserDefinedObject
    {
        get
        {
            if (HasSystemObjectFlag) return false;
            if (Owner is ISchemaCandidate schema)
            {
                if (schema.Name is "sys" or "dbo" or "INFORMATION_SCHEMA")
                    return false;
            }
            return true;
        }
    }
}
