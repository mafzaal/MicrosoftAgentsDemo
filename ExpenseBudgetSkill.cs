using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;

internal sealed class ExpenseBudgetSkill : AgentClassSkill<ExpenseBudgetSkill>
{
    private static readonly Dictionary<string, double> _limits = new()
    {
        ["Food"]          = 400,
        ["Transport"]     = 150,
        ["Housing"]       = 1500,
        ["Entertainment"] = 100,
        ["Health"]        = 200,
        ["Utilities"]     = 200,
        ["Shopping"]      = 300,
        ["Other"]         = 100,
    };

    private readonly ExpenseDb _db;

    public ExpenseBudgetSkill(ExpenseDb db) => _db = db;

    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "expense-budget",
        "Analyse monthly spending against budget targets, flag overspend, and suggest savings. " +
        "Use when the user asks about budgets, spending limits, remaining allowance, or financial health.");

    protected override string Instructions => """
        Use this skill whenever the user asks about budgets, spending limits, or financial health.

        Steps:
        1. Read the `budget-targets` resource for the recommended monthly spending limits per category.
        2. Run the `check-budget` script with the month in YYYY-MM format.
           - If the user does not specify a month, use the current month.
        3. For each category: show amount spent, budget limit, percentage used, and whether it is within budget.
        4. Calculate and show the overall total vs total budget.
        5. For over-budget categories, suggest one actionable tip to reduce spending in that category.
        """;

    [AgentSkillResource("budget-targets")]
    [Description("Recommended monthly spending limits per expense category.")]
    public string BudgetTargets => """
        # Monthly Budget Targets

        | Category      | Monthly Limit |
        |---------------|--------------|
        | Food          | $400          |
        | Transport     | $150          |
        | Housing       | $1,500        |
        | Entertainment | $100          |
        | Health        | $200          |
        | Utilities     | $200          |
        | Shopping      | $300          |
        | Other         | $100          |
        | **Total**     | **$2,950**    |
        """;

    [AgentSkillScript("check-budget")]
    [Description("Query actual spending for a given month (YYYY-MM) and compare each category against its budget. " +
                 "Returns JSON with per-category breakdowns and overall totals.")]
    private string CheckBudget(string month)
    {
        using var conn = _db.Open();
        using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT category, ROUND(SUM(amount), 2) AS total
            FROM expenses
            WHERE strftime('%Y-%m', date) = $month
            GROUP BY category
            ORDER BY total DESC
            """;
        cmd.Parameters.AddWithValue("$month", month);

        var categories = new List<object>();
        double totalSpent  = 0;
        double totalBudget = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cat   = reader.GetString(0);
            var spent = reader.GetDouble(1);
            var limit = _limits.TryGetValue(cat, out var l) ? l : 100.0;
            var pct   = Math.Round(spent / limit * 100, 1);

            categories.Add(new
            {
                category  = cat,
                spent     = spent,
                budget    = limit,
                used_pct  = pct,
                status    = spent <= limit ? "within_budget" : "over_budget",
                over_by   = spent > limit ? Math.Round(spent - limit, 2) : 0.0,
            });

            totalSpent  += spent;
            totalBudget += limit;
        }

        if (categories.Count == 0)
            return JsonSerializer.Serialize(new { month, message = "No expenses recorded for this month.", categories });

        return JsonSerializer.Serialize(new
        {
            month,
            categories,
            totals = new
            {
                spent     = Math.Round(totalSpent, 2),
                budget    = totalBudget,
                used_pct  = totalBudget > 0 ? Math.Round(totalSpent / totalBudget * 100, 1) : 0.0,
                remaining = Math.Round(totalBudget - totalSpent, 2),
                status    = totalSpent <= totalBudget ? "within_budget" : "over_budget",
            },
        });
    }
}
