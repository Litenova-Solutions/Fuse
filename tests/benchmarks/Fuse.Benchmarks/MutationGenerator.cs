using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Benchmarks;

/// <summary>
///     One generated single-file edit with a compiler-verified verdict, for the check-honesty gate (H1). A
///     breaking case has at least one error in the edited file; a neutral case leaves the whole compilation
///     clean. The verdict is not asserted by the operator, it is confirmed against the baseline compilation, so
///     the ground truth is mechanical rather than hand-labeled.
/// </summary>
/// <param name="Name">A stable, unique case name (operator plus sequence).</param>
/// <param name="OperatorId">The mutation operator that produced the case.</param>
/// <param name="ShouldBeClean">True for a neutral edit, false for a breaking edit.</param>
/// <param name="TargetFile">The edited document's path.</param>
/// <param name="NewContent">The full mutated source of the edited document.</param>
public sealed record MutationCase(
    string Name,
    string OperatorId,
    bool ShouldBeClean,
    string TargetFile,
    string NewContent);

/// <summary>
///     Generates breaking and neutral single-file mutants of a clean baseline compilation for the check-honesty
///     gate (H1). Mutation operators are Roslyn syntax rewrites; each candidate is verified against the compiler
///     before it is kept, so a breaking case is one the compiler actually rejects (with an error located in the
///     edited file) and a neutral case is one the compiler actually accepts. This is how "false green 0, false
///     red under 1 percent" becomes a measured claim over thousands of cases instead of eight hand-built ones.
/// </summary>
/// <remarks>
///     Generation is deterministic for a given seed: operators and targets are chosen from a seeded
///     <see cref="Random" />, so a recorded run reproduces. Operators are deliberately single-file, because the
///     shipped <c>fuse_check</c> contract is a single-file edit; a breaking mutant is kept only when an error
///     lands in the edited document, so the gate fairly tests the single-file classifier rather than crediting
///     a cross-file break the contract does not promise to see.
/// </remarks>
public sealed class MutationGenerator
{
    // Each class draws from this many attempts per requested case before giving up, so a fixture too small to
    // yield the requested count fails loudly (returns fewer) rather than looping forever.
    private const int AttemptsPerCase = 40;

    /// <summary>
    ///     Generates up to <paramref name="perClass" /> breaking and <paramref name="perClass" /> neutral verified
    ///     mutants over the mutable documents of <paramref name="baseline" />.
    /// </summary>
    /// <param name="baseline">A compilation that compiles clean (the caller must verify this first).</param>
    /// <param name="mutableFiles">The document paths eligible for mutation (the fixture's own sources, not references).</param>
    /// <param name="perClass">The number of cases to produce per class.</param>
    /// <param name="seed">The deterministic seed.</param>
    /// <returns>The verified cases (breaking first, then neutral); may be fewer than requested if the fixture is small.</returns>
    public IReadOnlyList<MutationCase> Generate(
        CSharpCompilation baseline, IReadOnlyCollection<string> mutableFiles, int perClass, int seed)
    {
        var rng = new Random(seed);
        var trees = baseline.SyntaxTrees
            .Where(t => mutableFiles.Contains(t.FilePath))
            .ToList();
        if (trees.Count == 0)
            return [];

        var cases = new List<MutationCase>(perClass * 2);
        cases.AddRange(GenerateClass(baseline, trees, perClass, rng, breaking: true));
        cases.AddRange(GenerateClass(baseline, trees, perClass, rng, breaking: false));
        return cases;
    }

    /// <summary>
    ///     Generates up to <paramref name="count" /> behavior mutants (T1 H1 extension): edits that still compile
    ///     clean but change runtime behavior (a negated condition, a flipped or off-by-one comparison), so a test
    ///     that covers the mutated code should fail. Unlike the break/neutral classes, "behavior changed" is not
    ///     compiler-verifiable, so the kill is confirmed by running the covering tests; here each candidate is only
    ///     verified to still compile (a behavior mutant that stops compiling would be a break, not a behavior test).
    /// </summary>
    /// <param name="baseline">A compilation that compiles clean.</param>
    /// <param name="mutableFiles">The document paths eligible for mutation.</param>
    /// <param name="count">The number of behavior mutants to produce.</param>
    /// <param name="seed">The deterministic seed.</param>
    /// <returns>The verified-compiling behavior mutants; may be fewer than requested if the fixture is small.</returns>
    public IReadOnlyList<MutationCase> GenerateBehaviorMutants(
        CSharpCompilation baseline, IReadOnlyCollection<string> mutableFiles, int count, int seed)
    {
        var rng = new Random(seed);
        var trees = baseline.SyntaxTrees.Where(t => mutableFiles.Contains(t.FilePath)).ToList();
        if (trees.Count == 0)
            return [];

        var cases = new List<MutationCase>(count);
        var produced = 0;
        var attempts = 0;
        var cap = count * AttemptsPerCase;
        while (produced < count && attempts < cap)
        {
            attempts++;
            var tree = trees[rng.Next(trees.Count)];
            var op = BehaviorOperators[rng.Next(BehaviorOperators.Length)];
            var root = tree.GetRoot();
            var mutated = op.Apply(root, rng);
            if (mutated is null)
                continue;

            var newText = mutated.ToFullString();
            if (newText == root.ToFullString())
                continue;

            // A behavior mutant must still compile clean; if it broke compilation it would be a break case.
            if (Classify(baseline, tree, newText, breaking: false) is null)
                continue;

            produced++;
            cases.Add(new MutationCase(
                $"behavior-{op.Id}-{produced}", op.Id, ShouldBeClean: true, tree.FilePath, newText));
        }

        return cases;
    }

    private static IEnumerable<MutationCase> GenerateClass(
        CSharpCompilation baseline, IReadOnlyList<SyntaxTree> trees, int perClass, Random rng, bool breaking)
    {
        var operators = breaking ? BreakingOperators : NeutralOperators;
        var produced = 0;
        var attempts = 0;
        var cap = perClass * AttemptsPerCase;
        while (produced < perClass && attempts < cap)
        {
            attempts++;
            var tree = trees[rng.Next(trees.Count)];
            var op = operators[rng.Next(operators.Length)];
            var root = tree.GetRoot();
            var mutated = op.Apply(root, rng);
            if (mutated is null)
                continue;

            var newText = mutated.ToFullString();
            if (newText == root.ToFullString())
                continue;

            var verdict = Classify(baseline, tree, newText, breaking);
            if (verdict is null)
                continue;

            produced++;
            yield return new MutationCase(
                $"{(breaking ? "break" : "neutral")}-{op.Id}-{produced}",
                op.Id,
                ShouldBeClean: !breaking,
                tree.FilePath,
                newText);
        }
    }

    // Applies a candidate to the baseline and returns the case only when the compiler confirms the intended
    // class: for a breaking candidate, at least one error located in the edited file; for a neutral candidate,
    // no errors anywhere. Returns null to reject a candidate whose real verdict does not match (so the operator
    // need not be perfect; the compiler is the ground truth).
    private static bool? Classify(CSharpCompilation baseline, SyntaxTree oldTree, string newText, bool breaking)
    {
        var newTree = CSharpSyntaxTree.ParseText(newText, path: oldTree.FilePath);
        var patched = baseline.ReplaceSyntaxTree(oldTree, newTree);
        var errors = patched.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (breaking)
        {
            var errorInEditedFile = errors.Any(d =>
                d.Location.SourceTree?.FilePath == newTree.FilePath);
            return errorInEditedFile ? true : null;
        }

        return errors.Count == 0 ? true : null;
    }

    private sealed record MutationOperator(string Id, Func<SyntaxNode, Random, SyntaxNode?> Apply);

    // Breaking operators. Each returns a mutated root or null when no applicable target exists in the tree. The
    // compiler verdict (not the operator) decides whether a produced candidate is kept, so an operator that
    // occasionally yields a still-compiling edit simply has that candidate discarded.
    private static readonly MutationOperator[] BreakingOperators =
    [
        // Reference a renamed member: rewrite a member-access name to one that cannot bind (CS1061).
        new("rename-member", static (root, rng) =>
        {
            var accesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().ToList();
            if (accesses.Count == 0)
                return null;
            var target = accesses[rng.Next(accesses.Count)];
            var renamed = target.WithName(SyntaxFactory.IdentifierName(target.Name.Identifier.Text + "_MUTX"));
            return root.ReplaceNode(target, renamed);
        }),

        // Change an expression's type: replace a returned expression with a string literal. Kept only when the
        // method's declared type is not string (the compiler rejects the assignment, CS0029); discarded otherwise.
        new("wrong-type-return", static (root, rng) =>
        {
            var returns = root.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Where(r => r.Expression is not null && r.Expression is not LiteralExpressionSyntax)
                .ToList();
            if (returns.Count == 0)
                return null;
            var target = returns[rng.Next(returns.Count)];
            var literal = SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("__mutation_wrong_type"));
            return root.ReplaceNode(target, target.WithExpression(literal));
        }),

        // Delete a required declaration: remove a private member. A private member's references are in-file by
        // language rule, so a deletion that leaves a reference breaks the edited file (CS0103/CS1061); a private
        // member with no references produces no error and is discarded by verification.
        new("delete-private-member", static (root, rng) =>
        {
            var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax)
                .Where(m => m.Modifiers.Any(SyntaxKind.PrivateKeyword))
                .ToList();
            if (members.Count == 0)
                return null;
            var target = members[rng.Next(members.Count)];
            return root.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia);
        }),

        // Reference an undefined type: replace a variable-declaration type with a name that cannot resolve (CS0246).
        new("undefined-type", static (root, rng) =>
        {
            var locals = root.DescendantNodes().OfType<VariableDeclarationSyntax>()
                .Where(v => v.Type is not null && !v.Type.IsVar)
                .ToList();
            if (locals.Count == 0)
                return null;
            var target = locals[rng.Next(locals.Count)];
            var undefined = SyntaxFactory.ParseTypeName("Undefined_MUTX")
                .WithTriviaFrom(target.Type);
            return root.ReplaceNode(target.Type, undefined);
        }),
    ];

    // Neutral operators. Each returns a mutated root that should preserve semantics; verification keeps only the
    // candidates the compiler still accepts, so an operator that occasionally perturbs behavior is self-correcting.
    private static readonly MutationOperator[] NeutralOperators =
    [
        // Comment insertion: a leading comment on a member. The null edit that must stay clean.
        new("insert-comment", static (root, rng) =>
        {
            var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToList();
            if (members.Count == 0)
                return null;
            var target = members[rng.Next(members.Count)];
            var comment = SyntaxFactory.Comment("// mutation-neutral marker\r\n");
            var newLeading = target.GetLeadingTrivia().Add(comment);
            return root.ReplaceNode(target, target.WithLeadingTrivia(newLeading));
        }),

        // Whitespace insertion: an extra blank line before a member. Semantically inert.
        new("insert-whitespace", static (root, rng) =>
        {
            var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToList();
            if (members.Count == 0)
                return null;
            var target = members[rng.Next(members.Count)];
            var blank = SyntaxFactory.EndOfLine("\r\n");
            var newLeading = target.GetLeadingTrivia().Insert(0, blank);
            return root.ReplaceNode(target, target.WithLeadingTrivia(newLeading));
        }),

        // Reorder members: swap two adjacent members within a type. Member order does not change semantics.
        new("reorder-members", static (root, rng) =>
        {
            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(t => t.Members.Count >= 2)
                .ToList();
            if (types.Count == 0)
                return null;
            var type = types[rng.Next(types.Count)];
            var i = rng.Next(type.Members.Count - 1);
            var members = type.Members.ToList();
            var first = members[i];
            var second = members[i + 1];
            // Swap positions and keep each slot's original trivia, so the reorder is a formatting-neutral member
            // reshuffle. Rebuilt as a fresh list to avoid SyntaxList.Replace's stale-node identity error.
            members[i] = second.WithTriviaFrom(first);
            members[i + 1] = first.WithTriviaFrom(second);
            return root.ReplaceNode(type, type.WithMembers(SyntaxFactory.List(members)));
        }),

        // Redundant parentheses around a returned expression: a value-preserving rewrite.
        new("redundant-parens", static (root, rng) =>
        {
            var returns = root.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Where(r => r.Expression is not null and not ParenthesizedExpressionSyntax)
                .ToList();
            if (returns.Count == 0)
                return null;
            var target = returns[rng.Next(returns.Count)];
            var parens = SyntaxFactory.ParenthesizedExpression(target.Expression!.WithoutTrivia())
                .WithTriviaFrom(target.Expression!);
            return root.ReplaceNode(target, target.WithExpression(parens));
        }),
    ];

    // Behavior operators (T1 H1 extension). Each returns a mutated root that still compiles but changes runtime
    // behavior, so a test covering the mutated code should fail. The kill is confirmed by running the covering
    // tests, not by the compiler.
    private static readonly MutationOperator[] BehaviorOperators =
    [
        // Negate a boolean condition: wrap an if/while condition in a logical-not, flipping the branch taken.
        new("negate-condition", static (root, rng) =>
        {
            var conditions = root.DescendantNodes()
                .Select(n => n switch
                {
                    IfStatementSyntax ifStatement => ifStatement.Condition,
                    WhileStatementSyntax whileStatement => whileStatement.Condition,
                    _ => null,
                })
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();
            if (conditions.Count == 0)
                return null;
            var target = conditions[rng.Next(conditions.Count)];
            var negated = SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(target.WithoutTrivia())).WithTriviaFrom(target);
            return root.ReplaceNode(target, negated);
        }),

        // Flip a relational operator to its off-by-one or opposite form, changing a boundary or the whole branch.
        new("flip-relational", static (root, rng) =>
        {
            var binaries = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
                .Where(b => b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                    or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression)
                .ToList();
            if (binaries.Count == 0)
                return null;
            var target = binaries[rng.Next(binaries.Count)];
            var (kind, tokenKind) = target.Kind() switch
            {
                SyntaxKind.LessThanExpression => (SyntaxKind.LessThanOrEqualExpression, SyntaxKind.LessThanEqualsToken),
                SyntaxKind.LessThanOrEqualExpression => (SyntaxKind.LessThanExpression, SyntaxKind.LessThanToken),
                SyntaxKind.GreaterThanExpression => (SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.GreaterThanEqualsToken),
                _ => (SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanToken),
            };
            var operatorToken = SyntaxFactory.Token(tokenKind).WithTriviaFrom(target.OperatorToken);
            var flipped = SyntaxFactory.BinaryExpression(kind, target.Left, operatorToken, target.Right);
            return root.ReplaceNode(target, flipped);
        }),
    ];
}
