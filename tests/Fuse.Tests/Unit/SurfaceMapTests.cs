using Fuse.Check;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Tests.Unit;

public class SurfaceMapTests
{
    private static (bool Broad, HashSet<string> Names) Diff(string before, string after) =>
        SurfaceMap.Diff(
            SurfaceMap.Compute(CSharpSyntaxTree.ParseText(before, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)),
            SurfaceMap.Compute(CSharpSyntaxTree.ParseText(after, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)));

    [Fact]
    public void Body_edit_changes_nothing()
    {
        var (broad, names) = Diff("class C { public int M() => 1; }", "class C { public int M() { return 2; } }");
        Assert.False(broad);
        Assert.Empty(names);
    }

    [Fact]
    public void Comments_and_formatting_change_nothing()
    {
        var (broad, names) = Diff("class C { public int M(int a) => a; }", "class C\n{\n    // doc\n    public int M( int a ) => a;\n}");
        Assert.False(broad);
        Assert.Empty(names);
    }

    [Fact]
    public void Renamed_method_reports_both_names_and_the_type()
    {
        var (broad, names) = Diff("class C { public int Add() => 1; }", "class C { public int Plus() => 1; }");
        Assert.False(broad);
        Assert.Equal(["Add", "C", "Plus"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Parameter_change_is_name_bound()
    {
        var (broad, names) = Diff("class C { public void M(int a) {} }", "class C { public void M(long a) {} }");
        Assert.False(broad);
        Assert.Contains("M", names);
    }

    [Fact]
    public void Accessibility_change_is_name_bound()
    {
        var (_, names) = Diff("class C { public void M() {} }", "class C { internal void M() {} }");
        Assert.Contains("M", names);
    }

    [Fact]
    public void Constant_value_is_part_of_the_surface()
    {
        var (_, names) = Diff("class C { public const int X = 1; }", "class C { public const int X = 2; }");
        Assert.Contains("X", names);
    }

    [Fact]
    public void Field_initializer_is_not_part_of_the_surface()
    {
        var (_, names) = Diff("class C { public int X = 1; }", "class C { public int X = 2; }");
        Assert.Empty(names);
    }

    [Fact]
    public void Base_list_change_is_broad()
    {
        var (broad, _) = Diff("class B {} class C { }", "class B {} class C : B { }");
        Assert.True(broad);
    }

    [Fact]
    public void Operator_change_is_broad()
    {
        var (broad, _) = Diff("struct V { }", "struct V { public static V operator +(V a, V b) => a; }");
        Assert.True(broad);
    }

    [Fact]
    public void Global_using_change_is_broad()
    {
        var (broad, _) = Diff("global using System;", "global using System.Text;");
        Assert.True(broad);
    }

    [Fact]
    public void Added_type_is_name_bound()
    {
        var (broad, names) = Diff("namespace N { class A {} }", "namespace N { class A {} class B {} }");
        Assert.False(broad);
        Assert.Equal(["B"], names);
    }

    [Fact]
    public void Enum_member_value_is_part_of_the_surface()
    {
        var (_, names) = Diff("enum E { A = 1 }", "enum E { A = 2 }");
        Assert.Equal(["A", "E"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Overloads_are_distinguished()
    {
        var (_, names) = Diff("class C { void M(int a) {} void M(string s) {} }", "class C { void M(int a) {} }");
        Assert.Contains("M", names);
    }
}
