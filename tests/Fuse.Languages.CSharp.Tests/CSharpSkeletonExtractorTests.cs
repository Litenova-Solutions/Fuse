using Fuse.Languages.CSharp.Skeleton;

namespace Fuse.Languages.CSharp.Tests.Skeleton;

public class CSharpSkeletonExtractorTests
{
    private readonly CSharpSkeletonExtractor _extractor = new();

    [Fact]
    public void ExtractSkeleton_PreservesClassDeclaration()
    {
        const string input = """
            public class OrderService
            {
                public void DoWork() { }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("class OrderService", result);
    }

    [Fact]
    public void ExtractSkeleton_SuppressesMethodBodies()
    {
        const string input = """
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                    var y = 2;
                }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.DoesNotContain("var x = 1", result);
        Assert.Contains("// ...", result);
    }

    [Fact]
    public void ExtractSkeleton_PreservesMethodSignatures()
    {
        const string input = """
            public class Svc
            {
                public Task<Order> CreateAsync(string name) { return Task.FromResult(new Order()); }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("CreateAsync", result);
    }

    [Fact]
    public void ExtractSkeleton_PreservesPropertyDeclarations()
    {
        const string input = """
            public class Foo
            {
                public string Name { get; set; }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("Name { get; set; }", result);
    }

    [Fact]
    public void ExtractSkeleton_PublicApiOnly_OmitsPrivateMembers()
    {
        const string input = """
            public class ApiSurface
            {
                public void PublicMethod() { }
                private void PrivateMethod() { }
                internal void InternalMethod() { }
            }
            """;

        var result = _extractor.ExtractSkeleton(input, publicApiOnly: true);

        Assert.Contains("PublicMethod", result);
        Assert.DoesNotContain("PrivateMethod", result);
        Assert.DoesNotContain("InternalMethod", result);
    }

    [Fact]
    public void ExtractSkeleton_PreservesInterface()
    {
        const string input = """
            public interface IFoo
            {
                void Execute();
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("interface IFoo", result);
        Assert.Contains("Execute()", result);
    }

    [Fact]
    public void ExtractSkeleton_PreservesImplementedInterfaces()
    {
        const string input = "public class Foo : IFoo, IBar { }";
        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("IFoo, IBar", result);
    }

    [Fact]
    public void ExtractSkeleton_PreservesConstructorSignature()
    {
        const string input = """
            public class Foo
            {
                public Foo(IRepo repo, ILogger logger) { _repo = repo; }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("Foo(IRepo repo", result);
        Assert.DoesNotContain("_repo = repo", result);
    }

    [Fact]
    public void ExtractSkeleton_HandlesNestedTypes()
    {
        const string input = """
            public class Outer
            {
                public class Inner { public void M() { } }
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("class Outer", result);
        Assert.Contains("class Inner", result);
    }

    [Fact]
    public void ExtractSkeleton_EmptyFile_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, _extractor.ExtractSkeleton(string.Empty));
    }

    [Fact]
    public void ExtractSkeleton_EnumDeclaration_PreservedWithMembers()
    {
        const string input = """
            public enum Status
            {
                Active,
                Inactive
            }
            """;

        var result = _extractor.ExtractSkeleton(input);
        Assert.Contains("enum Status", result);
        Assert.Contains("Active", result);
    }
}

public class Order { }
