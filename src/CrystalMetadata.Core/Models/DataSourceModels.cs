using System.Collections.Generic;

namespace CrystalMetadata.Core.Models
{
    /// <summary>Database definition of the report: connections, tables (incl. commands / stored procedures), fields and joins.</summary>
    public sealed class DataSourceMetadata
    {
        public List<ConnectionMetadata> Connections { get; set; } = new List<ConnectionMetadata>();
        public List<TableMetadata> Tables { get; set; } = new List<TableMetadata>();
        public List<TableLinkMetadata> Links { get; set; } = new List<TableLinkMetadata>();

        /// <summary>SQL Crystal would generate ("Show SQL Query"). Only populated when <c>includeGeneratedSql</c> is enabled.</summary>
        public string? GeneratedSql { get; set; }
    }

    /// <summary>A distinct connection used by one or more tables. Passwords are never captured.</summary>
    public sealed class ConnectionMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string? ServerName { get; set; }
        public string? DatabaseName { get; set; }
        public string? UserId { get; set; }
        public bool? IntegratedSecurity { get; set; }
        public string? DatabaseDll { get; set; }
        public string? Provider { get; set; }
        public string? ConnectionType { get; set; }
        public Dictionary<string, string?> LogonProperties { get; set; } = new Dictionary<string, string?>();
        public Dictionary<string, object?> Attributes { get; set; } = new Dictionary<string, object?>();
    }

    public enum TableKind
    {
        Unknown,
        Table,
        View,
        StoredProcedure,
        Command
    }

    public sealed class TableMetadata
    {
        /// <summary>Alias used inside the report (this is what <c>{Alias.Field}</c> references).</summary>
        public string Alias { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Location { get; set; }
        public string? QualifiedName { get; set; }
        public TableKind Kind { get; set; }
        public string? ConnectionId { get; set; }

        /// <summary>Verbatim SQL of a Command object; null for normal tables.</summary>
        public string? CommandText { get; set; }

        public List<CommandParameterMetadata> CommandParameters { get; set; } = new List<CommandParameterMetadata>();
        public List<FieldMetadata> Fields { get; set; } = new List<FieldMetadata>();
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class CommandParameterMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string? ValueType { get; set; }
        public string? DefaultValue { get; set; }
    }

    /// <summary>A database field (column / SP result column).</summary>
    public sealed class FieldMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string TableAlias { get; set; } = string.Empty;
        public string? ValueType { get; set; }
        public int? LengthBytes { get; set; }

        /// <summary>Crystal does not expose nullability; always null.</summary>
        public bool? Nullable { get; set; }

        public int? UseCount { get; set; }
        public bool IsUsedInReport { get; set; }
        public string? HeadingText { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>A join between two tables as defined in the Database Expert.</summary>
    public sealed class TableLinkMetadata
    {
        public string? SourceTable { get; set; }
        public string? DestinationTable { get; set; }
        public List<string> SourceFields { get; set; } = new List<string>();
        public List<string> DestinationFields { get; set; } = new List<string>();
        public string? JoinType { get; set; }
        public string? LinkOperator { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }
}
