namespace VAppCore.Tests;

public class VRsqlParserTests
{
    private readonly VRsqlParser _parser = new();

    // ── Parsing ──

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(_parser.Parse(null));
        Assert.Null(_parser.Parse(""));
        Assert.Null(_parser.Parse("  "));
    }

    [Fact]
    public void Parse_SimpleEqual_ReturnsComparisonNode()
    {
        var node = _parser.Parse("name==John");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal("name", comparison.Selector);
        Assert.Equal(RsqlOperator.Equal, comparison.Operator);
        Assert.Equal(["John"], comparison.Arguments);
    }

    [Fact]
    public void Parse_NotEqual_ReturnsCorrectOperator()
    {
        var node = _parser.Parse("status!=Active");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.NotEqual, comparison.Operator);
    }

    [Theory]
    [InlineData("age=gt=30", RsqlOperator.GreaterThan)]
    [InlineData("age=ge=30", RsqlOperator.GreaterThanOrEqual)]
    [InlineData("age=lt=30", RsqlOperator.LessThan)]
    [InlineData("age=le=30", RsqlOperator.LessThanOrEqual)]
    public void Parse_ComparisonOperators_ParsesCorrectly(string rsql, RsqlOperator expectedOp)
    {
        var node = _parser.Parse(rsql);

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(expectedOp, comparison.Operator);
        Assert.Equal("30", comparison.Arguments[0]);
    }

    [Fact]
    public void Parse_InOperator_ParsesMultipleValues()
    {
        var node = _parser.Parse("status=in=(Active,Inactive,Suspended)");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.In, comparison.Operator);
        Assert.Equal(["Active", "Inactive", "Suspended"], comparison.Arguments);
    }

    [Fact]
    public void Parse_OutOperator_ParsesMultipleValues()
    {
        var node = _parser.Parse("status=out=(Suspended)");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.NotIn, comparison.Operator);
    }

    [Fact]
    public void Parse_LikeOperator_ParsesPattern()
    {
        var node = _parser.Parse("name=like=*John*");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.Like, comparison.Operator);
        Assert.Equal("*John*", comparison.Arguments[0]);
    }

    [Fact]
    public void Parse_IsNullOperator()
    {
        var node = _parser.Parse("department=isnull=true");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.IsNull, comparison.Operator);
    }

    [Fact]
    public void Parse_IsNotNullOperator()
    {
        var node = _parser.Parse("department=isnotnull=true");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal(RsqlOperator.IsNotNull, comparison.Operator);
    }

    [Fact]
    public void Parse_QuotedString_ParsesCorrectly()
    {
        var node = _parser.Parse("name=='John Doe'");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal("John Doe", comparison.Arguments[0]);
    }

    [Fact]
    public void Parse_DoubleQuotedString_ParsesCorrectly()
    {
        var node = _parser.Parse("name==\"John Doe\"");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal("John Doe", comparison.Arguments[0]);
    }

    [Fact]
    public void Parse_EscapedQuote_ParsesCorrectly()
    {
        var node = _parser.Parse("name=='it\\'s'");

        var comparison = Assert.IsType<ComparisonNode>(node);
        Assert.Equal("it's", comparison.Arguments[0]);
    }

    // ── Logical operators ──

    [Fact]
    public void Parse_AndOperator_ReturnsAndNode()
    {
        var node = _parser.Parse("name==John;age=gt=25");

        var andNode = Assert.IsType<AndNode>(node);
        Assert.Equal(2, andNode.Children.Count);
        Assert.All(andNode.Children, c => Assert.IsType<ComparisonNode>(c));
    }

    [Fact]
    public void Parse_OrOperator_ReturnsOrNode()
    {
        var node = _parser.Parse("name==John,name==Jane");

        var orNode = Assert.IsType<OrNode>(node);
        Assert.Equal(2, orNode.Children.Count);
    }

    [Fact]
    public void Parse_MixedAndOr_CorrectPrecedence()
    {
        // AND binds tighter: a==1,b==2;c==3 → OR(a==1, AND(b==2, c==3))
        var node = _parser.Parse("name==John,age=gt=25;isActive==true");

        var orNode = Assert.IsType<OrNode>(node);
        Assert.Equal(2, orNode.Children.Count);
        Assert.IsType<ComparisonNode>(orNode.Children[0]);
        Assert.IsType<AndNode>(orNode.Children[1]);
    }

    [Fact]
    public void Parse_Parentheses_OverridePrecedence()
    {
        var node = _parser.Parse("(name==John,name==Jane);age=gt=25");

        var andNode = Assert.IsType<AndNode>(node);
        Assert.Equal(2, andNode.Children.Count);
        Assert.IsType<OrNode>(andNode.Children[0]);
        Assert.IsType<ComparisonNode>(andNode.Children[1]);
    }

    [Fact]
    public void Parse_MultipleAndConditions()
    {
        var node = _parser.Parse("name==John;age=gt=25;isActive==true");

        var andNode = Assert.IsType<AndNode>(node);
        Assert.Equal(3, andNode.Children.Count);
    }

    // ── GetUsedFields ──

    [Fact]
    public void GetUsedFields_ReturnsAllSelectors()
    {
        var node = _parser.Parse("name==John;age=gt=25,email=like=*@test.com")!;

        var fields = node.GetUsedFields().ToList();
        Assert.Contains("name", fields);
        Assert.Contains("age", fields);
        Assert.Contains("email", fields);
    }

    // ── Expression building ──

    [Fact]
    public void ParseToExpression_Equal_Works()
    {
        var expr = _parser.ParseToExpression<User>("Name==John");
        Assert.NotNull(expr);

        var func = expr!.Compile();
        Assert.True(func(new User { Name = "John" }));
        Assert.False(func(new User { Name = "Jane" }));
    }

    [Fact]
    public void ParseToExpression_GreaterThan_Works()
    {
        var expr = _parser.ParseToExpression<User>("Age=gt=25");
        var func = expr!.Compile();

        Assert.True(func(new User { Age = 30 }));
        Assert.False(func(new User { Age = 20 }));
    }

    [Fact]
    public void ParseToExpression_LessThanOrEqual_Works()
    {
        var expr = _parser.ParseToExpression<User>("Age=le=25");
        var func = expr!.Compile();

        Assert.True(func(new User { Age = 25 }));
        Assert.True(func(new User { Age = 20 }));
        Assert.False(func(new User { Age = 30 }));
    }

    [Fact]
    public void ParseToExpression_Boolean_Works()
    {
        var expr = _parser.ParseToExpression<User>("IsActive==true");
        var func = expr!.Compile();

        Assert.True(func(new User { IsActive = true }));
        Assert.False(func(new User { IsActive = false }));
    }

    [Fact]
    public void ParseToExpression_DateTime_Works()
    {
        var expr = _parser.ParseToExpression<User>("CreatedAt=gt=2024-01-01");
        var func = expr!.Compile();

        Assert.True(func(new User { CreatedAt = new DateTime(2024, 6, 1) }));
        Assert.False(func(new User { CreatedAt = new DateTime(2023, 1, 1) }));
    }

    [Fact]
    public void ParseToExpression_Decimal_Works()
    {
        var expr = _parser.ParseToExpression<User>("Salary=ge=50000");
        var func = expr!.Compile();

        Assert.True(func(new User { Salary = 60000m }));
        Assert.False(func(new User { Salary = 40000m }));
    }

    [Fact]
    public void ParseToExpression_Enum_Works()
    {
        var expr = _parser.ParseToExpression<User>("Status==Active");
        var func = expr!.Compile();

        Assert.True(func(new User { Status = UserStatus.Active }));
        Assert.False(func(new User { Status = UserStatus.Inactive }));
    }

    [Fact]
    public void ParseToExpression_In_Works()
    {
        var expr = _parser.ParseToExpression<User>("Status=in=(Active,Suspended)");
        var func = expr!.Compile();

        Assert.True(func(new User { Status = UserStatus.Active }));
        Assert.True(func(new User { Status = UserStatus.Suspended }));
        Assert.False(func(new User { Status = UserStatus.Inactive }));
    }

    [Fact]
    public void ParseToExpression_NotIn_Works()
    {
        var expr = _parser.ParseToExpression<User>("Status=out=(Suspended)");
        var func = expr!.Compile();

        Assert.True(func(new User { Status = UserStatus.Active }));
        Assert.False(func(new User { Status = UserStatus.Suspended }));
    }

    [Fact]
    public void ParseToExpression_Like_Contains_Works()
    {
        var expr = _parser.ParseToExpression<User>("Name=like=*oh*");
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John" }));
        Assert.False(func(new User { Name = "Jane" }));
    }

    [Fact]
    public void ParseToExpression_Like_StartsWith_Works()
    {
        var expr = _parser.ParseToExpression<User>("Name=like=Jo*");
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John" }));
        Assert.False(func(new User { Name = "Jane" }));
    }

    [Fact]
    public void ParseToExpression_Like_EndsWith_Works()
    {
        var expr = _parser.ParseToExpression<User>("Name=like=*hn");
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John" }));
        Assert.False(func(new User { Name = "Jane" }));
    }

    [Fact]
    public void ParseToExpression_IsNull_Works()
    {
        var expr = _parser.ParseToExpression<User>("Department=isnull=true");
        var func = expr!.Compile();

        Assert.True(func(new User { Department = null }));
        Assert.False(func(new User { Department = "Engineering" }));
    }

    [Fact]
    public void ParseToExpression_IsNotNull_Works()
    {
        var expr = _parser.ParseToExpression<User>("Department=isnotnull=true");
        var func = expr!.Compile();

        Assert.True(func(new User { Department = "Engineering" }));
        Assert.False(func(new User { Department = null }));
    }

    [Fact]
    public void ParseToExpression_NotEqual_Works()
    {
        var expr = _parser.ParseToExpression<User>("Name!=John");
        var func = expr!.Compile();

        Assert.False(func(new User { Name = "John" }));
        Assert.True(func(new User { Name = "Jane" }));
    }

    [Fact]
    public void ParseToExpression_And_CombinesCorrectly()
    {
        var expr = _parser.ParseToExpression<User>("Name==John;Age=gt=25");
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John", Age = 30 }));
        Assert.False(func(new User { Name = "John", Age = 20 }));
        Assert.False(func(new User { Name = "Jane", Age = 30 }));
    }

    [Fact]
    public void ParseToExpression_Or_CombinesCorrectly()
    {
        var expr = _parser.ParseToExpression<User>("Name==John,Name==Jane");
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John" }));
        Assert.True(func(new User { Name = "Jane" }));
        Assert.False(func(new User { Name = "Bob" }));
    }

    [Fact]
    public void ParseToExpression_NestedProperty_Works()
    {
        var expr = _parser.ParseToExpression<User>("Address.City==London");
        var func = expr!.Compile();

        Assert.True(func(new User { Address = new Address { City = "London" } }));
        Assert.False(func(new User { Address = new Address { City = "Paris" } }));
    }

    // ── Error cases ──

    [Fact]
    public void Parse_InvalidProperty_ThrowsRsqlParseException()
    {
        // Property lookup happens during expression building
        Assert.Throws<RsqlParseException>(() =>
        {
            var expr = _parser.ParseToExpression<User>("NonExistent==value");
            expr!.Compile()(new User());
        });
    }

    [Fact]
    public void Parse_UnterminatedString_ThrowsRsqlParseException()
    {
        Assert.Throws<RsqlParseException>(() => _parser.Parse("name=='unterminated"));
    }

    [Fact]
    public void Parse_MissingOperator_ThrowsRsqlParseException()
    {
        Assert.Throws<RsqlParseException>(() => _parser.Parse("name"));
    }

    // ── Extension method ──

    [Fact]
    public void ApplyRsql_FiltersQueryable()
    {
        var users = new List<User>
        {
            new() { Name = "John", Age = 30 },
            new() { Name = "Jane", Age = 25 },
            new() { Name = "Bob", Age = 35 }
        }.AsQueryable();

        var result = users.ApplyRsql("Age=gt=28").ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, u => Assert.True(u.Age > 28));
    }

    [Fact]
    public void ApplyRsql_NullOrEmpty_ReturnsOriginal()
    {
        var users = new List<User>
        {
            new() { Name = "John" },
            new() { Name = "Jane" }
        }.AsQueryable();

        Assert.Equal(2, users.ApplyRsql(null).Count());
        Assert.Equal(2, users.ApplyRsql("").Count());
    }
}
