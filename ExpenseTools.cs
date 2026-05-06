using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>
/// Agent tools for managing personal expenses.
/// Each public method is registered as an <see cref="Microsoft.Extensions.AI.AIFunction"/>
/// and called by the LLM when the user asks to add, view, update, or delete expenses.
/// </summary>
public sealed class ExpenseTools(ExpenseDb db)
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    [Description("Add a new expense. Returns confirmation with the assigned ID.")]
    public string AddExpense(
        [Description("Amount spent (positive number, e.g. 12.50)")] decimal amount,
        [Description("Category: Food, Transport, Housing, Entertainment, Health, Utilities, Shopping, or Other")] string category,
        [Description("Short description of what was purchased or paid")] string description,
        [Description("Date in YYYY-MM-DD format. Omit to default to today.")] string? date = null)
    {
        if (amount <= 0) return "Error: amount must be positive.";

        var expenseDate = date ?? DateTime.Today.ToString("yyyy-MM-dd");

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expenses (amount, category, description, date)
            VALUES ($amount, $category, $description, $date)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$amount", (double)amount);
        cmd.Parameters.AddWithValue("$category", category.Trim());
        cmd.Parameters.AddWithValue("$description", description.Trim());
        cmd.Parameters.AddWithValue("$date", expenseDate);

        var id = cmd.ExecuteScalar();
        return $"Added expense #{id}: \"{description}\" — ${amount:F2} [{category}] on {expenseDate}";
    }

    [Description("List expenses, optionally filtered by category and/or date range. Returns up to `limit` results ordered newest first.")]
    public string GetExpenses(
        [Description("Filter by category (optional)")] string? category = null,
        [Description("Earliest date to include, YYYY-MM-DD (optional)")] string? startDate = null,
        [Description("Latest date to include, YYYY-MM-DD (optional)")] string? endDate = null,
        [Description("Maximum number of records to return (default 20)")] int limit = 20)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(category))   { where.Add("category = $cat");     cmd.Parameters.AddWithValue("$cat", category); }
        if (!string.IsNullOrWhiteSpace(startDate))   { where.Add("date >= $start");      cmd.Parameters.AddWithValue("$start", startDate); }
        if (!string.IsNullOrWhiteSpace(endDate))     { where.Add("date <= $end");        cmd.Parameters.AddWithValue("$end", endDate); }
        cmd.Parameters.AddWithValue("$limit", limit);

        cmd.CommandText = $"""
            SELECT id, amount, category, description, date
            FROM expenses
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY date DESC, id DESC
            LIMIT $limit
            """;

        var rows = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new
            {
                id          = reader.GetInt32(0),
                amount      = reader.GetDouble(1),
                category    = reader.GetString(2),
                description = reader.GetString(3),
                date        = reader.GetString(4)
            });
        }

        if (rows.Count == 0) return "No expenses found matching the filters.";
        return JsonSerializer.Serialize(rows, _jsonOpts);
    }

    [Description("Update one or more fields of an existing expense. Only the fields you supply are changed.")]
    public string UpdateExpense(
        [Description("ID of the expense to update")] int id,
        [Description("New amount (optional)")] decimal? amount = null,
        [Description("New category (optional)")] string? category = null,
        [Description("New description (optional)")] string? description = null,
        [Description("New date in YYYY-MM-DD format (optional)")] string? date = null)
    {
        var sets = new List<string>();
        if (amount.HasValue)                         sets.Add("amount = $amount");
        if (!string.IsNullOrWhiteSpace(category))    sets.Add("category = $category");
        if (!string.IsNullOrWhiteSpace(description)) sets.Add("description = $description");
        if (!string.IsNullOrWhiteSpace(date))        sets.Add("date = $date");

        if (sets.Count == 0) return "No fields provided to update.";

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE expenses SET {string.Join(", ", sets)} WHERE id = $id";
        if (amount.HasValue)                         cmd.Parameters.AddWithValue("$amount",      (double)amount.Value);
        if (!string.IsNullOrWhiteSpace(category))    cmd.Parameters.AddWithValue("$category",    category);
        if (!string.IsNullOrWhiteSpace(description)) cmd.Parameters.AddWithValue("$description", description);
        if (!string.IsNullOrWhiteSpace(date))        cmd.Parameters.AddWithValue("$date",        date);
        cmd.Parameters.AddWithValue("$id", id);

        var rows = cmd.ExecuteNonQuery();
        return rows == 0 ? $"No expense with ID {id} found." : $"Expense #{id} updated successfully.";
    }

    [Description("Permanently delete an expense by its ID.")]
    public string DeleteExpense(
        [Description("ID of the expense to delete")] int id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM expenses WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        var rows = cmd.ExecuteNonQuery();
        return rows == 0 ? $"No expense with ID {id} found." : $"Expense #{id} deleted.";
    }

    [Description("Get total spending grouped by category, with an overall total. Optionally filter by date range.")]
    public string GetExpenseSummary(
        [Description("Start date YYYY-MM-DD (optional)")] string? startDate = null,
        [Description("End date YYYY-MM-DD (optional)")] string? endDate = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(startDate)) { where.Add("date >= $start"); cmd.Parameters.AddWithValue("$start", startDate); }
        if (!string.IsNullOrWhiteSpace(endDate))   { where.Add("date <= $end");   cmd.Parameters.AddWithValue("$end", endDate); }

        cmd.CommandText = $"""
            SELECT category, SUM(amount) AS total, COUNT(*) AS count
            FROM expenses
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            GROUP BY category
            ORDER BY total DESC
            """;

        var sb = new StringBuilder();
        double grandTotal = 0;
        int grandCount = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cat   = reader.GetString(0);
            var total = reader.GetDouble(1);
            var count = reader.GetInt32(2);
            sb.AppendLine($"  {cat,-20} ${total,8:F2}  ({count} expense{(count == 1 ? "" : "s")})");
            grandTotal += total;
            grandCount += count;
        }

        if (grandCount == 0) return "No expenses found for the given period.";

        return $"Expense Summary\n" +
               $"{'─',40}\n" +
               sb +
               $"{'─',40}\n" +
               $"  {"Total",-20} ${grandTotal,8:F2}  ({grandCount} expense{(grandCount == 1 ? "" : "s")})";
    }
}
