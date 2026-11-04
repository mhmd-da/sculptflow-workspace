using System.Collections.Concurrent;
using Npgsql;

namespace Omni.Automation.Core;

/// <summary>
/// Optional direct access to the target's database, for what the API can't do or show: setting up a fake channel
/// connection, moving a 24-hour window or trial end into the past, reading usage records, traces and error logs.
/// Reads can look anywhere. Writes go through <see cref="ExecuteForClientAsync"/>, which refuses unless the client is an
/// automation client ("[Automation] …" with only @omni-automation.test users) — the same rule the main app's cleanup
/// endpoint uses. Each write's SQL must scope itself to that client id (pass it as @clientId).
/// </summary>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<Guid, bool> _verified = new();

    public Db(string connectionString, Action<string> log)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // Windows: Npgsql's GSS encryption probe hangs against Supabase's pooler and local Docker; nothing here uses GSS.
        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        builder.ApplicationName ??= "Omni.Automation";
        builder.Timeout = Math.Max(builder.Timeout, 30);
        _source = NpgsqlDataSource.Create(builder.ConnectionString);
        _log = log;
    }

    /// <summary>Rows as column → value dictionaries (DBNull becomes null).</summary>
    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, object? parameters = null, CancellationToken ct = default)
    {
        await using var cmd = Command(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        _log($"DB read: {Short(sql)} -> {rows.Count} row(s)");
        return rows;
    }

    /// <summary>The first column of the first row, or default.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, object? parameters = null, CancellationToken ct = default)
    {
        await using var cmd = Command(sql, parameters);
        var value = await cmd.ExecuteScalarAsync(ct);
        _log($"DB read: {Short(sql)}");
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>A write scoped to one automation client. The SQL gets @clientId automatically.</summary>
    public async Task<int> ExecuteForClientAsync(Guid clientId, string sql, object? parameters = null, CancellationToken ct = default)
    {
        await EnsureAutomationClientAsync(clientId, ct);
        var all = ToDictionary(parameters);
        all["clientId"] = clientId;
        await using var cmd = Command(sql, all);
        var count = await cmd.ExecuteNonQueryAsync(ct);
        _log($"DB write ({clientId}): {Short(sql)} -> {count} row(s)");
        return count;
    }

    private async Task EnsureAutomationClientAsync(Guid clientId, CancellationToken ct)
    {
        if (_verified.ContainsKey(clientId)) return;
        await using var cmd = Command("""
            select c.name, coalesce(bool_and(lower(u.email) like '%@' || @domain), false) as all_test_users
            from core.clients c
            left join identity.client_users cu on cu.client_id = c.id
            left join identity.users u on u.id = cu.user_id
            where c.id = @clientId
            group by c.name
            """, new Dictionary<string, object?> { ["clientId"] = clientId, ["domain"] = TestContext.EmailDomain });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new TestFailure($"DB write refused: client {clientId} doesn't exist.");
        var name = reader.GetString(0);
        var allTestUsers = reader.GetBoolean(1);
        if (!name.StartsWith(TestContext.ClientNamePrefix, StringComparison.Ordinal) || !allTestUsers)
        {
            throw new TestFailure($"DB write refused: \"{name}\" is not an automation client.");
        }
        _verified[clientId] = true;
    }

    private NpgsqlCommand Command(string sql, object? parameters)
    {
        var cmd = _source.CreateCommand(sql);
        foreach (var (name, value) in ToDictionary(parameters)) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static Dictionary<string, object?> ToDictionary(object? parameters) => parameters switch
    {
        null => new(),
        IDictionary<string, object?> d => new(d),
        _ => parameters.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(parameters)),
    };

    private static string Short(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
