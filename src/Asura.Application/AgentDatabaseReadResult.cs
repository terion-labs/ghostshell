namespace Asura.Application;

/// <summary>
/// A validated, detached result from one governed relational or Redis read.
/// </summary>
public abstract record AgentDatabaseReadResult
{
    private AgentDatabaseReadResult(string toolName)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }

    public sealed record State : AgentDatabaseReadResult
    {
        internal State(DatabasePanelSessionState value)
            : base(BuiltInAgentTools.DatabaseReadState)
        {
            Value = value;
        }

        public DatabasePanelSessionState Value { get; }
    }

    public sealed record Objects : AgentDatabaseReadResult
    {
        internal Objects(DatabaseObjectPage value)
            : base(BuiltInAgentTools.DatabaseListObjects)
        {
            Value = value;
        }

        public DatabaseObjectPage Value { get; }
    }

    public sealed record ObjectDescription : AgentDatabaseReadResult
    {
        internal ObjectDescription(DatabaseObjectSnapshot value)
            : base(BuiltInAgentTools.DatabaseDescribeObject)
        {
            Value = value;
        }

        public DatabaseObjectSnapshot Value { get; }
    }

    public sealed record Table : AgentDatabaseReadResult
    {
        internal Table(DatabaseTableSnapshot value)
            : base(BuiltInAgentTools.DatabaseReadTable)
        {
            Value = value;
        }

        public DatabaseTableSnapshot Value { get; }
    }

    public sealed record Schema : AgentDatabaseReadResult
    {
        internal Schema(DatabaseSchemaGraphSnapshot value)
            : base(BuiltInAgentTools.DatabaseSchemaGraph)
        {
            Value = value;
        }

        public DatabaseSchemaGraphSnapshot Value { get; }
    }

    public sealed record RedisKeys : AgentDatabaseReadResult
    {
        internal RedisKeys(RedisKeyPage value)
            : base(BuiltInAgentTools.RedisScan)
        {
            Value = value;
        }

        public RedisKeyPage Value { get; }
    }

    public sealed record RedisValue : AgentDatabaseReadResult
    {
        internal RedisValue(RedisKeyValueSnapshot value)
            : base(BuiltInAgentTools.RedisRead)
        {
            Value = value;
        }

        public RedisKeyValueSnapshot Value { get; }
    }

    public sealed record RedisSearch : AgentDatabaseReadResult
    {
        internal RedisSearch(RedisSearchResult value)
            : base(BuiltInAgentTools.RedisSearch)
        {
            Value = value;
        }

        public RedisSearchResult Value { get; }
    }

    public sealed record RedisIndexes : AgentDatabaseReadResult
    {
        internal RedisIndexes(RedisSearchIndexPage value)
            : base(BuiltInAgentTools.RedisListIndexes)
        {
            Value = value;
        }

        public RedisSearchIndexPage Value { get; }
    }
}
