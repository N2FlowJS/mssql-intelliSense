# Walkthrough: Multi-Connection Introspection & SQLite Cache Redesign

The GraphQL server database connection model has been redesigned to support multiple active SQL Server connections. SQLite (`MssqlIntelliSense.db`) now acts strictly as a persistent local store for the connection profiles and their scanned database schema caches (serialized as JSON).

## Redesigned Architecture

```mermaid
graph TD
    A[SSMS Active Connection] -->|Auto-Sync Mutation| B[GraphQL Server]
    C[Web UI Manual Connection] -->|Register Mutation| B
    B -->|Save Connection Info| D[(SQLite MssqlIntelliSense.db)]
    B -->|Trigger Scan| E[Introspector: SqlServerMetadataProvider]
    E -->|Switch Databases & Scan sys Tables| F[SQL Server Instance]
    E -->|Scanned JSON Cache| D
    B <-->|Loads Cache & Aggregates| G[AggregatedMetadataProvider]
    G <-->|GraphQL Resolvers| H[Query / Completion API]
```
