using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ledgerline.QueryPlans;

/// <summary>
/// EF Core's <c>ToQueryString()</c> declares parameters as SQL comments (<c>-- @p='2'</c>) and references
/// them as <c>@p</c>. This turns the comments back into real parameters so the statement can be explained.
/// </summary>
internal static partial class EfQueryText
{
    public static (string Sql, IReadOnlyList<PlanParameter> Parameters) Parse(string queryString)
    {
        var parameters = new List<PlanParameter>();
        var sql = new StringBuilder();
        foreach (var line in queryString.Split('\n'))
        {
            var declaration = Declaration().Match(line);
            if (!declaration.Success)
            {
                sql.AppendLine(line.TrimEnd('\r'));
                continue;
            }

            var value = declaration.Groups["value"].Value;
            parameters.Add(new PlanParameter(
                declaration.Groups["name"].Value,
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : value));
        }

        return (sql.ToString().Trim(), parameters);
    }

    [GeneratedRegex(@"^-- @(?<name>\w+)='(?<value>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
}
