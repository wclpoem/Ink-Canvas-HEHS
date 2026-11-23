using System.Globalization;
using System.Text;

namespace InkCanvas.Core.AiAssistant;

public enum VerificationStatus
{
    Verified,
    Unverified,
    LowConfidence,
}

public sealed record VerificationResult(VerificationStatus Status, string Note);

/// <summary>
/// 求解自检：对一元一次/一元二次代数方程做纯字符串解析，把求出的根代回原式校验。
/// 不依赖第三方库，任何异常输入都返回 Unverified 而不是抛出。
/// </summary>
public static class EquationVerifier
{
    private const double Tolerance = 1e-6;

    public static VerificationResult Verify(string formulaOrText)
    {
        try
        {
            return VerifyCore(formulaOrText);
        }
        catch (Exception)
        {
            return new VerificationResult(VerificationStatus.Unverified, "无法解析方程");
        }
    }

    public static VerificationResult Verify(string formulaOrText, double confidence)
    {
        if (confidence >= 0 && confidence < 0.5)
        {
            return new VerificationResult(
                VerificationStatus.LowConfidence,
                "置信度 " + Number(confidence) + " 低于 0.5，结果仅供参考");
        }

        return Verify(formulaOrText);
    }

    private static VerificationResult VerifyCore(string formulaOrText)
    {
        if (!TryNormalize(formulaOrText, out string normalized, out string error))
        {
            return Unverified(error);
        }

        int equals = normalized.IndexOf('=');
        if (equals < 0) return Unverified("未找到等号，无法解析");
        if (normalized.IndexOf('=', equals + 1) >= 0) return Unverified("包含多个等号，无法解析");

        string lhs = normalized.Substring(0, equals);
        string rhs = normalized.Substring(equals + 1);
        if (lhs.Length == 0 || rhs.Length == 0) return Unverified("等号两侧不完整");

        if (!TryDetectVariable(lhs, out char leftVariable, out error)) return Unverified(error);
        if (!TryDetectVariable(rhs, out char rightVariable, out error)) return Unverified(error);
        if (leftVariable != '\0' && rightVariable != '\0' && leftVariable != rightVariable)
        {
            return Unverified("未知数不一致（" + leftVariable + " / " + rightVariable + "）");
        }

        char variable = leftVariable != '\0' ? leftVariable : rightVariable;
        string name = variable == '\0' ? "x" : variable.ToString();

        if (!TryParsePolynomial(lhs, variable, out var left, out error)) return Unverified(error);
        if (!TryParsePolynomial(rhs, variable, out var right, out error)) return Unverified(error);

        var difference = new Dictionary<int, double>();
        foreach (var (power, coefficient) in left)
        {
            difference[power] = Get(difference, power) + coefficient;
        }

        foreach (var (power, coefficient) in right)
        {
            difference[power] = Get(difference, power) - coefficient;
        }

        foreach (int power in difference.Keys)
        {
            if (power > 2) return Unverified("仅支持一元一次或一元二次方程");
        }

        double a = Get(difference, 2);
        double b = Get(difference, 1);
        double c = Get(difference, 0);

        if (Math.Abs(a) > 1e-12)
        {
            return SolveQuadratic(a, b, c, left, right, name);
        }

        if (Math.Abs(b) > 1e-12)
        {
            return CheckRoots(new List<double> { -c / b }, left, right, name);
        }

        if (Math.Abs(c) <= Tolerance)
        {
            return new VerificationResult(VerificationStatus.Verified, "等式恒成立");
        }

        return Unverified("方程无解：" + Number(c) + " ≠ 0");
    }

    private static VerificationResult SolveQuadratic(
        double a,
        double b,
        double c,
        Dictionary<int, double> left,
        Dictionary<int, double> right,
        string name)
    {
        double discriminant = b * b - 4 * a * c;
        if (discriminant < -Tolerance)
        {
            return Unverified("判别式 Δ = " + Number(discriminant) + " < 0，方程无实根（复数根）");
        }

        double root = Math.Sqrt(Math.Max(0, discriminant));
        var roots = new List<double>();

        if (root <= Tolerance)
        {
            roots.Add(-b / (2 * a));
        }
        else
        {
            roots.Add((-b - root) / (2 * a));
            roots.Add((-b + root) / (2 * a));
        }

        return CheckRoots(roots, left, right, name);
    }

    private static VerificationResult CheckRoots(
        List<double> roots,
        Dictionary<int, double> left,
        Dictionary<int, double> right,
        string name)
    {
        var confirmed = new List<double>();
        foreach (double root in roots)
        {
            double error = Math.Abs(Evaluate(left, root) - Evaluate(right, root));
            if (error > Tolerance)
            {
                return Unverified("代入 " + name + "=" + Number(root) + " 不成立（误差 " + Number(error) + "）");
            }

            confirmed.Add(root);
        }

        confirmed.Sort();
        var parts = new List<string>(confirmed.Count);
        foreach (double value in confirmed)
        {
            parts.Add(name + "=" + Number(value));
        }

        return new VerificationResult(
            VerificationStatus.Verified,
            "代入 " + string.Join("、", parts) + " 成立");
    }

    private static bool TryNormalize(string raw, out string text, out string error)
    {
        text = "";
        error = "";

        string plain = AssistantText.LatexToPlain(raw ?? "");
        if (string.IsNullOrEmpty(plain))
        {
            error = "内容为空，无法解析";
            return false;
        }

        var output = new StringBuilder(plain.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            char c = plain[i];
            switch (c)
            {
                case '＝':
                    output.Append('=');
                    continue;
                case '＋':
                    output.Append('+');
                    continue;
                case '－':
                case '−':
                case '–':
                case '—':
                    output.Append('-');
                    continue;
                case '×':
                case '·':
                    output.Append('*');
                    continue;
                case ' ':
                case '\t':
                case '\u3000':
                case '$':
                    continue;
                case '⁰':
                    output.Append("^0");
                    continue;
                case '¹':
                    output.Append("^1");
                    continue;
                case '²':
                    output.Append("^2");
                    continue;
                case '³':
                    output.Append("^3");
                    continue;
                case '⁴':
                    output.Append("^4");
                    continue;
                case '⁵':
                    output.Append("^5");
                    continue;
                case '⁶':
                    output.Append("^6");
                    continue;
                case '⁷':
                    output.Append("^7");
                    continue;
                case '⁸':
                    output.Append("^8");
                    continue;
                case '⁹':
                    output.Append("^9");
                    continue;
            }

            bool allowed = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || c == '+'
                || c == '-'
                || c == '*'
                || c == '^'
                || c == '='
                || c == '.';
            if (!allowed)
            {
                error = "包含非代数符号“" + c + "”";
                return false;
            }

            output.Append(c);
        }

        text = output.ToString();
        if (text.Length == 0)
        {
            error = "内容为空，无法解析";
            return false;
        }

        return true;
    }

    private static bool TryDetectVariable(string expression, out char variable, out string error)
    {
        variable = '\0';
        error = "";

        foreach (char c in expression)
        {
            if (c < 'a' || c > 'z')
            {
                if (c < 'A' || c > 'Z') continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (variable == '\0')
            {
                variable = lower;
                continue;
            }

            if (variable != lower)
            {
                error = "包含多个未知数（" + variable + "、" + lower + "）";
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePolynomial(
        string expression,
        char variable,
        out Dictionary<int, double> coefficients,
        out string error)
    {
        coefficients = new Dictionary<int, double>();
        error = "";

        int index = 0;
        int sign = 1;
        bool anyTerm = false;
        bool needTerm = true;

        while (index < expression.Length)
        {
            char current = expression[index];
            if (current == '+')
            {
                sign = 1;
                index++;
                needTerm = true;
                continue;
            }

            if (current == '-')
            {
                sign = -1;
                index++;
                needTerm = true;
                continue;
            }

            if (!needTerm)
            {
                error = "表达式缺少运算符";
                return false;
            }

            double number = 0;
            bool hasNumber = false;
            if (char.IsDigit(current) || current == '.')
            {
                int start = index;
                while (index < expression.Length && (char.IsDigit(expression[index]) || expression[index] == '.'))
                {
                    index++;
                }

                string token = expression.Substring(start, index - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    error = "无法解析系数 " + token;
                    return false;
                }

                hasNumber = true;
            }

            if (index < expression.Length && expression[index] == '*')
            {
                index++;
                if (variable == '\0' ||
                    index >= expression.Length ||
                    char.ToLowerInvariant(expression[index]) != variable)
                {
                    error = "仅支持“系数×未知数”形式的乘法";
                    return false;
                }
            }

            bool hasVariable = false;
            int power = 0;
            if (index < expression.Length && variable != '\0' &&
                char.ToLowerInvariant(expression[index]) == variable)
            {
                hasVariable = true;
                index++;
                power = 1;

                if (index < expression.Length && expression[index] == '^')
                {
                    index++;
                    int start = index;
                    while (index < expression.Length && char.IsDigit(expression[index]))
                    {
                        index++;
                    }

                    if (start == index)
                    {
                        error = "指数缺失";
                        return false;
                    }

                    if (!int.TryParse(
                            expression.Substring(start, index - start),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out power) || power < 0)
                    {
                        error = "指数不合法";
                        return false;
                    }
                }
            }

            if (hasVariable)
            {
                if (!hasNumber) number = 1;
                coefficients[power] = Get(coefficients, power) + sign * number;
            }
            else if (hasNumber)
            {
                coefficients[0] = Get(coefficients, 0) + sign * number;
            }
            else
            {
                error = "无法解析表达式片段";
                return false;
            }

            anyTerm = true;
            needTerm = false;
            sign = 1;
        }

        if (!anyTerm || needTerm)
        {
            error = "表达式不完整";
            return false;
        }

        return true;
    }

    private static double Get(Dictionary<int, double> coefficients, int power)
    {
        return coefficients.TryGetValue(power, out double value) ? value : 0;
    }

    private static double Evaluate(Dictionary<int, double> coefficients, double variable)
    {
        double result = 0;
        foreach (var (power, coefficient) in coefficients)
        {
            result += coefficient * Math.Pow(variable, power);
        }

        return result;
    }

    private static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "NaN";

        double rounded = Math.Round(value);
        return Math.Abs(value - rounded) <= 1e-9
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static VerificationResult Unverified(string note)
    {
        return new VerificationResult(VerificationStatus.Unverified, note);
    }
}