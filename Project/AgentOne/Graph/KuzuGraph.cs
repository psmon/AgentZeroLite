using System.Runtime.InteropServices;
using static AgentOne.Graph.KuzuNative;

namespace AgentOne.Graph;

/// <summary>
/// A thin managed wrapper over one Kùzu database and one connection: run
/// Cypher, read rows back as strings. The connection is not thread-safe;
/// callers serialise (the knowledge graph does). Adapted from
/// C:\code\psmon\akka-graph-loop's KuzuGraph.
/// </summary>
public sealed class KuzuGraph : IDisposable
{
    private Database _db;
    private Connection _conn;
    private bool _disposed;

    public KuzuGraph(string databasePath, bool readOnly = false)
    {
        var config = kuzu_default_system_config();
        config.ReadOnly = readOnly;
        if (kuzu_database_init(databasePath, config, out _db) != Success)
            throw new InvalidOperationException($"could not open the Kùzu database at {databasePath}");
        if (kuzu_connection_init(ref _db, out _conn) != Success)
        {
            kuzu_database_destroy(ref _db);
            throw new InvalidOperationException("could not open a Kùzu connection");
        }
    }

    /// <summary>Runs Cypher whose result nobody reads (DDL, CREATE, SET).</summary>
    public void Execute(string cypher)
    {
        RunChecked(cypher, out var result);
        kuzu_query_result_destroy(ref result);
    }

    /// <summary>Runs Cypher with bound parameters (<c>$name</c>): string, long/int, or double values.</summary>
    public void Execute(string cypher, IReadOnlyDictionary<string, object> parameters)
    {
        var result = RunPrepared(cypher, parameters);
        kuzu_query_result_destroy(ref result);
    }

    /// <summary>Rows of <paramref name="columns"/> strings each.</summary>
    public List<string[]> Query(string cypher, int columns)
    {
        RunChecked(cypher, out var result);
        return ReadRows(ref result, columns);
    }

    public List<string[]> Query(string cypher, int columns, IReadOnlyDictionary<string, object> parameters)
    {
        var result = RunPrepared(cypher, parameters);
        return ReadRows(ref result, columns);
    }

    /// <summary>
    /// An explicit transaction. Disposed without <see cref="Commit"/> it rolls
    /// back, so an exception midway leaves no half-written knowledge behind.
    /// </summary>
    public Transaction Begin() => new(this);

    public sealed class Transaction : IDisposable
    {
        private readonly KuzuGraph _graph;
        private bool _settled;

        internal Transaction(KuzuGraph graph)
        {
            _graph = graph;
            _graph.Execute("BEGIN TRANSACTION");
        }

        public void Commit()
        {
            if (_settled) return;
            _settled = true;
            _graph.Execute("COMMIT");
        }

        public void Dispose()
        {
            if (_settled) return;
            _settled = true;
            try { _graph.Execute("ROLLBACK"); }
            catch (InvalidOperationException) { /* already aborted; keep the original exception */ }
        }
    }

    private QueryResult RunPrepared(string cypher, IReadOnlyDictionary<string, object> parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kuzu_connection_prepare(ref _conn, cypher, out var prepared) != Success ||
            kuzu_prepared_statement_is_success(ref prepared) == 0)
        {
            var msg = Marshal.PtrToStringUTF8(kuzu_prepared_statement_get_error_message(ref prepared)) ?? "(unknown error)";
            kuzu_prepared_statement_destroy(ref prepared);
            throw new InvalidOperationException($"Kùzu prepare failed: {msg}\n  Cypher: {cypher}");
        }

        try
        {
            foreach (var (name, value) in parameters)
            {
                var state = value switch
                {
                    string s => kuzu_prepared_statement_bind_string(ref prepared, name, s),
                    long l => kuzu_prepared_statement_bind_int64(ref prepared, name, l),
                    int i => kuzu_prepared_statement_bind_int64(ref prepared, name, i),
                    double d => kuzu_prepared_statement_bind_double(ref prepared, name, d),
                    _ => throw new NotSupportedException($"unsupported parameter type: {value?.GetType().Name}"),
                };
                if (state != Success)
                    throw new InvalidOperationException($"Kùzu could not bind ${name}");
            }

            if (kuzu_connection_execute(ref _conn, ref prepared, out var result) != Success ||
                kuzu_query_result_is_success(ref result) == 0)
            {
                var msg = Marshal.PtrToStringUTF8(kuzu_query_result_get_error_message(ref result)) ?? "(unknown error)";
                kuzu_query_result_destroy(ref result);
                throw new InvalidOperationException($"Kùzu execute failed: {msg}\n  Cypher: {cypher}");
            }
            return result;
        }
        finally
        {
            kuzu_prepared_statement_destroy(ref prepared);
        }
    }

    private static List<string[]> ReadRows(ref QueryResult result, int columns)
    {
        var rows = new List<string[]>();
        try
        {
            while (kuzu_query_result_has_next(ref result) != 0)
            {
                kuzu_query_result_get_next(ref result, out var tuple);
                var row = new string[columns];
                for (var i = 0; i < columns; i++)
                {
                    kuzu_flat_tuple_get_value(ref tuple, (ulong)i, out var value);
                    var ptr = kuzu_value_to_string(ref value);
                    row[i] = Marshal.PtrToStringUTF8(ptr) ?? "";
                    kuzu_destroy_string(ptr);
                    kuzu_value_destroy(ref value);
                }
                rows.Add(row);
            }
        }
        finally
        {
            kuzu_query_result_destroy(ref result);
        }
        return rows;
    }

    private void RunChecked(string cypher, out QueryResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kuzu_connection_query(ref _conn, cypher, out result) != Success ||
            kuzu_query_result_is_success(ref result) == 0)
        {
            var message = Marshal.PtrToStringUTF8(kuzu_query_result_get_error_message(ref result)) ?? "(unknown error)";
            kuzu_query_result_destroy(ref result);
            throw new InvalidOperationException($"Kùzu query failed: {message}\n  Cypher: {cypher}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        kuzu_connection_destroy(ref _conn);
        kuzu_database_destroy(ref _db);
    }
}
