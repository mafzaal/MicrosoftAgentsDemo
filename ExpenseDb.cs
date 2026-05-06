using Microsoft.Data.Sqlite;

/// <summary>Opens SQLite connections and ensures the expenses schema exists.</summary>
public sealed class ExpenseDb
{
    public string ConnectionString { get; }

    public ExpenseDb(string dbPath)
    {
        ConnectionString = $"Data Source={dbPath}";
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS expenses (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                amount      REAL    NOT NULL,
                category    TEXT    NOT NULL,
                description TEXT    NOT NULL,
                date        TEXT    NOT NULL,
                created_at  TEXT    NOT NULL DEFAULT (date('now'))
            )
            """;
        cmd.ExecuteNonQuery();
    }
}
