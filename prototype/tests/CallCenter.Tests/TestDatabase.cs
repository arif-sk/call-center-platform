using Microsoft.Data.SqlClient;

namespace CallCenter.Tests;

/// <summary>
/// A throwaway SQL Server database per test class.
///
/// Integration tests that share a database share each other's failures, so each class gets its own,
/// created by the application's own schema bootstrap and dropped afterwards. The drop is forced —
/// the application pools connections, and SQL Server refuses to drop a database that still has any.
/// </summary>
public sealed class TestDatabase
{
    /// <summary>Override with CALLCENTER_TEST_SQL to run against LocalDB, a container or a build agent.</summary>
    private static string Server =>
        Environment.GetEnvironmentVariable("CALLCENTER_TEST_SQL")
        ?? "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True";

    public string Name { get; } = $"CallCenterTest_{Guid.NewGuid():N}";

    public string ConnectionString => $"{Server};Database={Name}";

    public void Drop()
    {
        try
        {
            using var connection = new SqlConnection($"{Server};Database=master");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Name}];
                END
                """;
            command.Parameters.AddWithValue("@name", Name);
            command.ExecuteNonQuery();
        }
        catch
        {
            // Best effort. A leftover test database is untidy, not a failing test.
        }
    }
}
