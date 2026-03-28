using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace VAppCore;

#region AST Nodes

public abstract class RsqlNode
{
    public abstract Expression<Func<T, bool>> ToLinqExpression<T>();

    /// <summary>
    /// Extracts all field/selector names used in this node and its children.
    /// </summary>
    public abstract IEnumerable<string> GetUsedFields();
}

public class ComparisonNode : RsqlNode
{
    public string Selector { get; set; } = string.Empty;
    public RsqlOperator Operator { get; set; }
    public List<string> Arguments { get; set; } = new();

    public override IEnumerable<string> GetUsedFields()
    {
        yield return Selector;
    }

    public override Expression<Func<T, bool>> ToLinqExpression<T>()
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var property = GetPropertyExpression<T>(parameter, Selector);
        var body = BuildComparisonExpression(property, Operator, Arguments);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static MemberExpression GetPropertyExpression<T>(ParameterExpression param, string selector)
    {
        // Support nested properties like "address.city"
        Expression expr = param;
        foreach (var part in selector.Split('.'))
        {
            var propInfo = expr.Type.GetProperty(part,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (propInfo == null)
                throw new RsqlParseException($"Property '{part}' not found on type '{expr.Type.Name}'");
            expr = Expression.Property(expr, propInfo);
        }
        return (MemberExpression)expr;
    }

    private static Expression BuildComparisonExpression(MemberExpression property, RsqlOperator op, List<string> args)
    {
        var propertyType = property.Type;
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        return op switch
        {
            RsqlOperator.Equal => BuildEqualExpression(property, args[0], underlyingType),
            RsqlOperator.NotEqual => Expression.Not(BuildEqualExpression(property, args[0], underlyingType)),
            RsqlOperator.GreaterThan => BuildCompareExpression(property, args[0], underlyingType, ExpressionType.GreaterThan),
            RsqlOperator.GreaterThanOrEqual => BuildCompareExpression(property, args[0], underlyingType, ExpressionType.GreaterThanOrEqual),
            RsqlOperator.LessThan => BuildCompareExpression(property, args[0], underlyingType, ExpressionType.LessThan),
            RsqlOperator.LessThanOrEqual => BuildCompareExpression(property, args[0], underlyingType, ExpressionType.LessThanOrEqual),
            RsqlOperator.In => BuildInExpression(property, args, underlyingType),
            RsqlOperator.NotIn => Expression.Not(BuildInExpression(property, args, underlyingType)),
            RsqlOperator.Like => BuildLikeExpression(property, args[0]),
            RsqlOperator.ILike => BuildILikeExpression(property, args[0]),
            RsqlOperator.IsNull => BuildIsNullExpression(property),
            RsqlOperator.IsNotNull => Expression.Not(BuildIsNullExpression(property)),
            _ => throw new RsqlParseException($"Unsupported operator: {op}")
        };
    }

    private static Expression BuildEqualExpression(MemberExpression property, string value, Type targetType)
    {
        var convertedValue = ConvertValue(value, targetType);
        var constant = Expression.Constant(convertedValue, property.Type);
        return Expression.Equal(property, constant);
    }

    private static Expression BuildCompareExpression(MemberExpression property, string value, Type targetType, ExpressionType comparison)
    {
        var convertedValue = ConvertValue(value, targetType);
        var constant = Expression.Constant(convertedValue, property.Type);
        return Expression.MakeBinary(comparison, property, constant);
    }

    private static Expression BuildInExpression(MemberExpression property, List<string> values, Type targetType)
    {
        var convertedValues = values.Select(v => ConvertValue(v, targetType)).ToList();
        var listType = typeof(List<>).MakeGenericType(targetType);
        var list = Activator.CreateInstance(listType);
        var addMethod = listType.GetMethod("Add")!;
        foreach (var val in convertedValues)
            addMethod.Invoke(list, [val]);

        var containsMethod = listType.GetMethod("Contains")!;
        var listConstant = Expression.Constant(list);

        Expression propertyExpr = property;
        if (Nullable.GetUnderlyingType(property.Type) != null)
            propertyExpr = Expression.Property(property, "Value");

        return Expression.Call(listConstant, containsMethod, propertyExpr);
    }

    private static Expression BuildLikeExpression(MemberExpression property, string pattern)
    {
        // Convert RSQL wildcards to regex or use string methods
        // * = any characters, ? = single character
        if (property.Type != typeof(string))
            throw new RsqlParseException("LIKE operator can only be used with string properties");

        if (pattern.StartsWith('*') && pattern.EndsWith('*') && !pattern[1..^1].Contains('*'))
        {
            // *value* -> Contains
            var containsMethod = typeof(string).GetMethod("Contains", [typeof(string)])!;
            var value = pattern[1..^1];
            return Expression.Call(property, containsMethod, Expression.Constant(value));
        }
        if (pattern.StartsWith('*') && !pattern[1..].Contains('*'))
        {
            // *value -> EndsWith
            var endsWithMethod = typeof(string).GetMethod("EndsWith", [typeof(string)])!;
            var value = pattern[1..];
            return Expression.Call(property, endsWithMethod, Expression.Constant(value));
        }
        if (pattern.EndsWith('*') && !pattern[..^1].Contains('*'))
        {
            // value* -> StartsWith
            var startsWithMethod = typeof(string).GetMethod("StartsWith", [typeof(string)])!;
            var value = pattern[..^1];
            return Expression.Call(property, startsWithMethod, Expression.Constant(value));
        }

        // Complex pattern - use regex
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var isMatchMethod = typeof(Regex).GetMethod("IsMatch", [typeof(string), typeof(string)])!;
        return Expression.Call(isMatchMethod, property, Expression.Constant(regexPattern));
    }

    private static Expression BuildILikeExpression(MemberExpression property, string pattern)
    {
        // Case-insensitive LIKE using EF.Functions.ILike (PostgreSQL)
        if (property.Type != typeof(string))
            throw new RsqlParseException("ILIKE operator can only be used with string properties");

        // Convert * wildcards to SQL % wildcards
        var sqlPattern = pattern.Replace('*', '%').Replace('?', '_');

        // Get EF.Functions.ILike method from Npgsql
        var dbFunctionsType = typeof(EF).GetProperty("Functions")!.PropertyType;
        var iLikeMethod = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            "ILike",
            [dbFunctionsType, typeof(string), typeof(string)])!;

        // EF.Functions.ILike(property, pattern)
        var efFunctions = Expression.Property(null, typeof(EF), "Functions");
        return Expression.Call(iLikeMethod, efFunctions, property, Expression.Constant(sqlPattern));
    }

    private static Expression BuildIsNullExpression(MemberExpression property)
    {
        return Expression.Equal(property, Expression.Constant(null, property.Type));
    }

    private static object? ConvertValue(string value, Type targetType)
    {
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;

        if (targetType == typeof(string))
            return value;
        if (targetType == typeof(int))
            return int.Parse(value);
        if (targetType == typeof(long))
            return long.Parse(value);
        if (targetType == typeof(double))
            return double.Parse(value);
        if (targetType == typeof(decimal))
            return decimal.Parse(value);
        if (targetType == typeof(bool))
            return bool.Parse(value);
        if (targetType == typeof(DateTime))
            return DateTime.Parse(value);
        if (targetType == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(value);
        if (targetType == typeof(Guid))
            return Guid.Parse(value);
        if (targetType.IsEnum)
            return Enum.Parse(targetType, value, true);

        throw new RsqlParseException($"Cannot convert value '{value}' to type '{targetType.Name}'");
    }
}

public class AndNode : RsqlNode
{
    public List<RsqlNode> Children { get; set; } = new();

    public override IEnumerable<string> GetUsedFields()
    {
        return Children.SelectMany(c => c.GetUsedFields());
    }

    public override Expression<Func<T, bool>> ToLinqExpression<T>()
    {
        if (Children.Count == 0)
            return x => true;

        var parameter = Expression.Parameter(typeof(T), "x");
        Expression? combined = null;

        foreach (var child in Children)
        {
            var childExpr = child.ToLinqExpression<T>();
            var body = ReplaceParameter(childExpr.Body, childExpr.Parameters[0], parameter);
            combined = combined == null ? body : Expression.AndAlso(combined, body);
        }

        return Expression.Lambda<Func<T, bool>>(combined!, parameter);
    }

    private static Expression ReplaceParameter(Expression expr, ParameterExpression oldParam, ParameterExpression newParam)
    {
        return new ParameterReplacer(oldParam, newParam).Visit(expr);
    }
}

public class OrNode : RsqlNode
{
    public List<RsqlNode> Children { get; set; } = new();

    public override IEnumerable<string> GetUsedFields()
    {
        return Children.SelectMany(c => c.GetUsedFields());
    }

    public override Expression<Func<T, bool>> ToLinqExpression<T>()
    {
        if (Children.Count == 0)
            return x => false;

        var parameter = Expression.Parameter(typeof(T), "x");
        Expression? combined = null;

        foreach (var child in Children)
        {
            var childExpr = child.ToLinqExpression<T>();
            var body = new ParameterReplacer(childExpr.Parameters[0], parameter).Visit(childExpr.Body);
            combined = combined == null ? body : Expression.OrElse(combined, body);
        }

        return Expression.Lambda<Func<T, bool>>(combined!, parameter);
    }
}

#endregion

#region Helper Classes

public enum RsqlOperator
{
    Equal,          // ==
    NotEqual,       // !=
    GreaterThan,    // =gt= or >
    GreaterThanOrEqual, // =ge= or >=
    LessThan,       // =lt= or <
    LessThanOrEqual,    // =le= or <=
    In,             // =in=
    NotIn,          // =out=
    Like,           // =like=
    ILike,          // =ilike= (case-insensitive)
    IsNull,         // =isnull=
    IsNotNull       // =isnotnull=
}

internal class ParameterReplacer(ParameterExpression oldParam, ParameterExpression newParam) : ExpressionVisitor
{
    protected override Expression VisitParameter(ParameterExpression node)
    {
        return node == oldParam ? newParam : base.VisitParameter(node);
    }
}

public class RsqlParseException : Exception
{
    public RsqlParseException(string message) : base(message) { }
    public RsqlParseException(string message, Exception inner) : base(message, inner) { }
}

#endregion

#region Tokenizer

internal enum TokenType
{
    Identifier,
    Operator,
    Value,
    And,
    Or,
    OpenParen,
    CloseParen,
    Comma,
    End
}

internal record Token(TokenType Type, string Value, int Position);

internal class RsqlTokenizer
{
    private readonly string _input;
    private int _position;

    private static readonly Dictionary<string, RsqlOperator> Operators = new()
    {
        ["=="] = RsqlOperator.Equal,
        ["!="] = RsqlOperator.NotEqual,
        ["=gt="] = RsqlOperator.GreaterThan,
        ["=ge="] = RsqlOperator.GreaterThanOrEqual,
        ["=lt="] = RsqlOperator.LessThan,
        ["=le="] = RsqlOperator.LessThanOrEqual,
        ["=in="] = RsqlOperator.In,
        ["=out="] = RsqlOperator.NotIn,
        ["=like="] = RsqlOperator.Like,
        ["=ilike="] = RsqlOperator.ILike,
        ["=isnull="] = RsqlOperator.IsNull,
        ["=isnotnull="] = RsqlOperator.IsNotNull
    };

    public RsqlTokenizer(string input)
    {
        _input = input ?? string.Empty;
        _position = 0;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_position < _input.Length)
        {
            SkipWhitespace();
            if (_position >= _input.Length)
                break;

            var token = ReadNextToken();
            tokens.Add(token);
        }

        tokens.Add(new Token(TokenType.End, "", _position));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
            _position++;
    }

    private Token ReadNextToken()
    {
        var startPos = _position;
        var ch = _input[_position];

        // Single-char tokens
        switch (ch)
        {
            case '(': _position++; return new Token(TokenType.OpenParen, "(", startPos);
            case ')': _position++; return new Token(TokenType.CloseParen, ")", startPos);
            case ';': _position++; return new Token(TokenType.And, ";", startPos);
            case ',': _position++; return new Token(TokenType.Comma, ",", startPos);
        }

        // Check for operators
        foreach (var (opStr, _) in Operators.OrderByDescending(x => x.Key.Length))
        {
            if (_input[_position..].StartsWith(opStr, StringComparison.OrdinalIgnoreCase))
            {
                _position += opStr.Length;
                return new Token(TokenType.Operator, opStr, startPos);
            }
        }

        // Read identifier or value (supports alphanumeric, dots, underscores, hyphens, wildcards)
        if (ch == '"' || ch == '\'')
        {
            return ReadQuotedString(ch);
        }

        var value = ReadUnquotedValue();
        return new Token(TokenType.Identifier, value, startPos);
    }

    private Token ReadQuotedString(char quote)
    {
        var startPos = _position;
        _position++; // Skip opening quote
        var sb = new System.Text.StringBuilder();

        while (_position < _input.Length && _input[_position] != quote)
        {
            if (_input[_position] == '\\' && _position + 1 < _input.Length)
            {
                _position++;
                sb.Append(_input[_position]);
            }
            else
            {
                sb.Append(_input[_position]);
            }
            _position++;
        }

        if (_position >= _input.Length)
            throw new RsqlParseException($"Unterminated string at position {startPos}");

        _position++; // Skip closing quote
        return new Token(TokenType.Value, sb.ToString(), startPos);
    }

    private string ReadUnquotedValue()
    {
        var startPos = _position;
        while (_position < _input.Length && IsValueChar(_input[_position]))
            _position++;

        if (_position == startPos)
            throw new RsqlParseException($"Unexpected character '{_input[_position]}' at position {_position}");

        return _input[startPos.._position];
    }

    private static bool IsValueChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' || ch == '*' || ch == '?' || ch == '@' || ch == ':' || ch == '/';
    }

    public static RsqlOperator GetOperator(string op)
    {
        if (Operators.TryGetValue(op.ToLowerInvariant(), out var result))
            return result;
        throw new RsqlParseException($"Unknown operator: {op}");
    }
}

#endregion

#region Parser

public class VRsqlParser
{
    private List<Token> _tokens = new();
    private int _current;

    public RsqlNode? Parse(string? rsql)
    {
        if (string.IsNullOrWhiteSpace(rsql))
            return null;

        var tokenizer = new RsqlTokenizer(rsql);
        _tokens = tokenizer.Tokenize();
        _current = 0;

        return ParseOr();
    }

    public Expression<Func<T, bool>>? ParseToExpression<T>(string? rsql)
    {
        var node = Parse(rsql);
        return node?.ToLinqExpression<T>();
    }

    private RsqlNode ParseOr()
    {
        var left = ParseAnd();

        var children = new List<RsqlNode> { left };
        while (Match(TokenType.Comma))
        {
            children.Add(ParseAnd());
        }

        if (children.Count == 1)
            return children[0];

        return new OrNode { Children = children };
    }

    private RsqlNode ParseAnd()
    {
        var left = ParsePrimary();

        var children = new List<RsqlNode> { left };
        while (Match(TokenType.And))
        {
            children.Add(ParsePrimary());
        }

        if (children.Count == 1)
            return children[0];

        return new AndNode { Children = children };
    }

    private RsqlNode ParsePrimary()
    {
        if (Match(TokenType.OpenParen))
        {
            var node = ParseOr();
            Expect(TokenType.CloseParen, "Expected ')'");
            return node;
        }

        return ParseComparison();
    }

    private ComparisonNode ParseComparison()
    {
        var selectorToken = Expect(TokenType.Identifier, "Expected selector");
        var operatorToken = Expect(TokenType.Operator, "Expected operator");
        var args = ParseArguments();

        return new ComparisonNode
        {
            Selector = selectorToken.Value,
            Operator = RsqlTokenizer.GetOperator(operatorToken.Value),
            Arguments = args
        };
    }

    private List<string> ParseArguments()
    {
        var args = new List<string>();

        if (Check(TokenType.OpenParen))
        {
            // Multi-value: =in=(val1,val2,val3)
            Advance();
            args.Add(Expect(TokenType.Identifier, "Expected value").Value);

            while (Match(TokenType.Comma))
            {
                args.Add(Expect(TokenType.Identifier, "Expected value").Value);
            }

            Expect(TokenType.CloseParen, "Expected ')'");
        }
        else
        {
            // Single value
            var token = Advance();
            if (token.Type == TokenType.Identifier || token.Type == TokenType.Value)
            {
                args.Add(token.Value);
            }
            else
            {
                throw new RsqlParseException($"Expected value at position {token.Position}");
            }
        }

        return args;
    }

    private bool Match(TokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool Check(TokenType type)
    {
        return !IsAtEnd() && Peek().Type == type;
    }

    private Token Advance()
    {
        if (!IsAtEnd())
            _current++;
        return Previous();
    }

    private bool IsAtEnd() => Peek().Type == TokenType.End;
    private Token Peek() => _tokens[_current];
    private Token Previous() => _tokens[_current - 1];

    private Token Expect(TokenType type, string message)
    {
        if (Check(type))
            return Advance();

        var current = Peek();
        throw new RsqlParseException($"{message} at position {current.Position}, got '{current.Value}'");
    }
}

#endregion

#region IQueryable Extensions

public static class RsqlQueryableExtensions
{
    private static readonly VRsqlParser Parser = new();

    public static IQueryable<T> ApplyRsql<T>(this IQueryable<T> query, string? rsql)
    {
        if (string.IsNullOrWhiteSpace(rsql))
            return query;

        var expression = Parser.ParseToExpression<T>(rsql);
        return expression != null ? query.Where(expression) : query;
    }
}

#endregion