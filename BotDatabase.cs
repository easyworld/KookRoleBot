using Microsoft.Data.Sqlite;

namespace KookRoleBot;

public sealed class BotDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public BotDatabase(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS RoleExpirations (
                GuildId INTEGER NOT NULL,
                UserId INTEGER NOT NULL,
                RoleId INTEGER NOT NULL,
                ExpiresAt TEXT NOT NULL,
                PRIMARY KEY (GuildId, UserId, RoleId)
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DateTime?> GetExpirationAsync(ulong guildId, ulong userId, uint roleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ExpiresAt FROM RoleExpirations WHERE GuildId = $g AND UserId = $u AND RoleId = $r";
        cmd.Parameters.AddWithValue("$g", (long)guildId);
        cmd.Parameters.AddWithValue("$u", (long)userId);
        cmd.Parameters.AddWithValue("$r", (long)roleId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is string s && DateTime.TryParse(s, out var dt))
            return dt;
        return null;
    }

    public async Task SetExpirationAsync(ulong guildId, ulong userId, uint roleId, DateTime expiresAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO RoleExpirations (GuildId, UserId, RoleId, ExpiresAt)
            VALUES ($g, $u, $r, $e)
            """;
        cmd.Parameters.AddWithValue("$g", (long)guildId);
        cmd.Parameters.AddWithValue("$u", (long)userId);
        cmd.Parameters.AddWithValue("$r", (long)roleId);
        cmd.Parameters.AddWithValue("$e", expiresAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(ulong GuildId, ulong UserId, uint RoleId)>> GetExpiredRolesAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT GuildId, UserId, RoleId FROM RoleExpirations WHERE ExpiresAt <= $now";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var results = new List<(ulong, ulong, uint)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (uint)reader.GetInt64(2)));
        }
        return results;
    }

    public async Task RemoveExpirationAsync(ulong guildId, ulong userId, uint roleId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM RoleExpirations WHERE GuildId = $g AND UserId = $u AND RoleId = $r";
        cmd.Parameters.AddWithValue("$g", (long)guildId);
        cmd.Parameters.AddWithValue("$u", (long)userId);
        cmd.Parameters.AddWithValue("$r", (long)roleId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.CloseAsync();
        await _connection.DisposeAsync();
    }
}
