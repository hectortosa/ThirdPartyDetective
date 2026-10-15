using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class SpdxLicenseExpressionAdapterTests
{
    private readonly ISpdxLicenseExpressionAdapter _adapter = new SpdxLicenseExpressionAdapter();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOASSERTION")]
    [InlineData("NONE")]
    public void Empty_or_noassertion_returns_null_normalized(string? input)
    {
        var info = _adapter.Normalize(input);
        Assert.Null(info.Normalized);
        Assert.Empty(info.SubExpressions);
        Assert.False(info.IsCompound);
        Assert.Null(info.WithException);
    }

    // The declared identifier is what ATTRIBUTION.txt must name, so -only,
    // -or-later and + are preserved (the policy bucket ignores them).
    [Theory]
    [InlineData("MIT", "MIT")]
    [InlineData("Apache-2.0", "Apache-2.0")]
    [InlineData("GPL-3.0-or-later", "GPL-3.0-or-later")]
    [InlineData("LGPL-2.1-only", "LGPL-2.1-only")]
    [InlineData("AGPL-3.0+", "AGPL-3.0+")]
    public void Single_expression_keeps_the_declared_identifier(string input, string expected)
    {
        var info = _adapter.Normalize(input);
        Assert.Equal(expected, info.Normalized);
        Assert.False(info.IsCompound);
    }

    [Fact]
    public void Nested_expression_keeps_its_structure()
    {
        var info = _adapter.Normalize("(MIT OR Apache-2.0) AND BSD-2-Clause");
        Assert.Equal("(MIT OR Apache-2.0) AND BSD-2-Clause", info.Normalized);
        Assert.True(info.IsCompound);
        Assert.Equal(new[] { "MIT", "Apache-2.0", "BSD-2-Clause" }, info.SubExpressions);
    }

    [Fact]
    public void Lowercase_operators_are_normalized()
    {
        var info = _adapter.Normalize("mit or apache-2.0");
        Assert.Equal("mit OR apache-2.0", info.Normalized);
        Assert.True(info.IsCompound);
        Assert.Equal(new[] { "mit", "apache-2.0" }, info.SubExpressions);
    }

    [Fact]
    public void Free_text_is_kept_verbatim()
    {
        var info = _adapter.Normalize("Apache License, Version 2.0");
        Assert.Equal("Apache License, Version 2.0", info.Normalized);
        Assert.False(info.IsCompound);
    }

    [Fact]
    public void Compound_OR_expression_splits_into_sub_expressions()
    {
        var info = _adapter.Normalize("MIT OR Apache-2.0");
        Assert.True(info.IsCompound);
        Assert.Equal(new[] { "MIT", "Apache-2.0" }, info.SubExpressions);
    }

    [Fact]
    public void Compound_AND_expression_splits_into_sub_expressions()
    {
        var info = _adapter.Normalize("GPL-2.0 AND MIT");
        Assert.True(info.IsCompound);
        Assert.Contains("GPL-2.0", info.SubExpressions);
        Assert.Contains("MIT", info.SubExpressions);
    }

    [Fact]
    public void With_exception_is_captured_separately()
    {
        var info = _adapter.Normalize("Apache-2.0 WITH LLVM-exception");
        Assert.Equal("Apache-2.0 WITH LLVM-exception", info.Normalized);
        Assert.Equal(new[] { "Apache-2.0" }, info.SubExpressions);
        Assert.Equal("LLVM-exception", info.WithException);
        Assert.Equal(new[] { "LLVM-exception" }, info.Exceptions);
    }

    [Fact]
    public void LicenseRef_passes_through_unchanged()
    {
        var info = _adapter.Normalize("LicenseRef-Custom-Foo");
        Assert.Equal("LicenseRef-Custom-Foo", info.Normalized);
    }

    [Fact]
    public void Outer_parentheses_are_stripped()
    {
        var info = _adapter.Normalize("(MIT OR Apache-2.0)");
        Assert.True(info.IsCompound);
        Assert.Equal(new[] { "MIT", "Apache-2.0" }, info.SubExpressions);
    }
}
