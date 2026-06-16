using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;

namespace Playground.App;

// The monolith's own (direct, in-process) view of the synthetic member.
// Mirrors the API limb's SQL access so the "direct vs API" comparison is fair.
public static class Db
{
    public const string MemberId = "1";
    public const string MemberName = "Jordan A. Member";
    public const string MemberSsn = "640-01-2345"; // synthetic

    public static async Task EnsureSqlSeed(string conn, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(conn)) return;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                await using var c = new SqlConnection(conn);
                await c.OpenAsync();
                var cmd = c.CreateCommand();
                cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Members')
    CREATE TABLE Members (Id NVARCHAR(16) PRIMARY KEY, Name NVARCHAR(128), Ssn NVARCHAR(16));
IF NOT EXISTS (SELECT * FROM Members WHERE Id = @id)
    INSERT INTO Members (Id, Name, Ssn) VALUES (@id, @name, @ssn);";
                cmd.Parameters.AddWithValue("@id", MemberId);
                cmd.Parameters.AddWithValue("@name", MemberName);
                cmd.Parameters.AddWithValue("@ssn", MemberSsn);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex) when (attempt < 8)
            {
                log?.LogWarning("SQL seed attempt {Attempt} failed: {Msg}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    public static async Task<string?> ReadSsnSql(string conn, string id)
    {
        await using var c = new SqlConnection(conn);
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Ssn FROM Members WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    // Direct (in-process) Cosmos read — same backend the API limb reads, but no
    // hop. The API limb owns seeding; the body only reads.
    public static async Task<string?> ReadSsnCosmos(Container container, string id)
    {
        try
        {
            var resp = await container.ReadItemAsync<MemberDoc>(id, new PartitionKey(id));
            return resp.Resource.ssn;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public record MemberDoc(string id, string name, string ssn);
}
