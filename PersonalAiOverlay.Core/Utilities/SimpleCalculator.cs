using System.Data;
using System.Text.RegularExpressions;

namespace PersonalAiOverlay.Core.Utilities;

public static class SimpleCalculator
{
    private static readonly Regex SafeChars = new(@"^[0-9\.\+\-\*\/\(\)\s]+$", RegexOptions.Compiled);

    public static bool TryEval(string expression, out double value, out string error)
    {
        value = 0;
        error = "";

        expression = (expression ?? "").Trim();
        if (expression.Length == 0)
        {
            error = "Empty expression.";
            return false;
        }

        if (!SafeChars.IsMatch(expression))
        {
            error = "Only numbers and + - * / ( ) are allowed.";
            return false;
        }

        try
        {
            // DataTable.Compute is fine for a local toy calc once we strictly whitelist chars above.
            var dt = new DataTable();
            var result = dt.Compute(expression, "");
            value = Convert.ToDouble(result);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

