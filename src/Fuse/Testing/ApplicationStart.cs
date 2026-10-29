using System.Collections.Immutable;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>
///     Decides, per test class, whether the class starts an application, so a change that reaches an application runs
///     the test classes that start it whole and selects the rest by name. A class counts as not starting the
///     application only when the walk proves it: a class that shows a host shape runs whole, and a class the walk
///     cannot tell keeps the project whole.
/// </summary>
internal static class ApplicationStart
{
    /// <summary>Whether one test class starts an application.</summary>
    public enum Classification
    {
        /// <summary>The class starts the application: a factory of it, its process, its startup type, or its entry type.</summary>
        Starts,

        /// <summary>The walk proved the class starts nothing: no host shape, no factory, no process start, no entry type, and no base class with any.</summary>
        DoesNot,

        /// <summary>The walk cannot tell: a process started under a name it cannot read, an assembly or type loaded by name, or a base class outside the test project.</summary>
        CannotBeTold,
    }

    /// <summary>
    ///     Resolves the tests a change that reaches <paramref name="application"/> selects in <paramref name="test"/>:
    ///     every test class that starts the application is selected whole, and the rest keep the tests the walks
    ///     selected by name. A project whose classes cannot be enumerated, or with a class the walk cannot tell, runs
    ///     whole with <paramref name="reason"/>, as does a test application reached in itself, whose host is the test run.
    /// </summary>
    public static async Task ResolveAsync(
        Solution solution,
        ProjectNode test,
        ProjectNode application,
        string reason,
        SelectionBuilder selections,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (selections.IsWhole(test))
            return;
        if (test.Path == application.Path)
        {
            selections.SelectWhole(test, reason);
            return;
        }

        var variants = RepoWorkspace.ProjectsFor(solution, test).ToList();
        if (variants.Count == 0)
        {
            selections.SelectWhole(test, reason);
            log($"test selection: {test.Name}: the test project is not loaded; running the project whole");
            return;
        }

        var (assemblies, files) = IdentityOf(application, solution);
        bool IsApplicationType(INamedTypeSymbol type) =>
            type.ContainingAssembly?.Name is { } assembly && assemblies.Contains(assembly);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var starts = new HashSet<string>(StringComparer.Ordinal);
        var doubtful = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in variants)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                selections.SelectWhole(test, reason);
                log($"test selection: {test.Name}: the test project is not loaded; running the project whole");
                return;
            }

            var classes = await TestClassesAsync(project, compilation, cancellationToken).ConfigureAwait(false);
            if (classes is null)
            {
                selections.SelectWhole(test, reason);
                log($"test selection: {test.Name}: no test class found; running the project whole");
                return;
            }

            foreach (var testClass in classes)
            {
                var name = MemberWalk.TestName(testClass);
                names.Add(name);
                switch (Classify(testClass, compilation, IsApplicationType, files, cancellationToken))
                {
                    case Classification.Starts:
                        starts.Add(name);
                        break;
                    case Classification.CannotBeTold:
                        doubtful.Add(name);
                        break;
                    default:
                        break;
                }
            }
        }

        if (doubtful.Count > 0)
        {
            selections.SelectWhole(test, reason);
            log($"test selection: {test.Name}: {doubtful.Count} of {names.Count} test class(es) may start {application.Name}; running the project whole");
            return;
        }

        foreach (var name in starts.OrderBy(n => n, StringComparer.Ordinal))
            selections.Add(test, name + ".");
        log($"test selection: {test.Name}: {starts.Count} of {names.Count} test class(es) start {application.Name}; selecting them whole, the rest by name");
    }

    /// <summary>
    ///     Merges resolved application selections into <paramref name="selections"/>: a resolved whole project replaces
    ///     what the walks selected in it, and resolved class prefixes join the project's patterns, replacing the names
    ///     they cover. A project already whole keeps it.
    /// </summary>
    public static void MergeInto(IReadOnlyDictionary<RepoPath, TestSelection> resolved, IDictionary<RepoPath, TestSelection> selections)
    {
        foreach (var (path, selection) in resolved)
        {
            switch (selection)
            {
                case TestSelection.Whole whole:
                    selections[path] = whole;
                    break;
                case TestSelection.Methods methods:
                    if (!selections.TryGetValue(path, out var existing))
                    {
                        selections[path] = selection;
                    }
                    else if (existing is TestSelection.Methods current)
                    {
                        var patterns = current.Patterns;
                        foreach (var pattern in methods.Patterns)
                        {
                            if (patterns.Contains(pattern) || patterns.Any(p => Covers(p, pattern)))
                                continue;
                            patterns = patterns.Where(p => !Covers(pattern, p)).ToImmutableHashSet().Add(pattern);
                        }

                        selections[path] = current with { Patterns = patterns };
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     Decides whether <paramref name="testClass"/> starts an application: it walks the class, its bases and every
    ///     type of the test project its members mention, and answers what the walk showed. Only a walk that finds no
    ///     host shape at all answers <see cref="Classification.DoesNot"/>.
    /// </summary>
    /// <param name="testClass">The test class, from <paramref name="compilation"/>.</param>
    /// <param name="compilation">The test project's compilation, which binds the walk.</param>
    /// <param name="isApplicationType">True for a type of the application, read from its containing assembly.</param>
    /// <param name="applicationFileNames">The file names the application's process starts under.</param>
    public static Classification Classify(
        INamedTypeSymbol testClass,
        Compilation compilation,
        Func<INamedTypeSymbol, bool> isApplicationType,
        IReadOnlySet<string> applicationFileNames,
        CancellationToken cancellationToken) =>
        new Walker(compilation, isApplicationType, applicationFileNames, cancellationToken).Classify(testClass);

    /// <summary>
    ///     The test classes of <paramref name="project"/>: every class, record or struct that declares or inherits a test
    ///     method, ordered by test name. Null when no class can be enumerated or a document cannot be read, so the caller
    ///     runs the project whole.
    /// </summary>
    private static async Task<List<INamedTypeSymbol>?> TestClassesAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var classes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = root is null ? null : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
                return null;
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (declaration is not (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax))
                    continue;
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
                    return null;
                if (!DeclaresOrInheritsTest(type))
                    continue;
                classes.Add(type);
            }
        }

        return classes.Count == 0 ? null : [.. classes.OrderBy(MemberWalk.TestName, StringComparer.Ordinal)];
    }

    /// <summary>True when <paramref name="type"/> or any base type declares a test method, which the type then runs under its own name.</summary>
    private static bool DeclaresOrInheritsTest(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetMembers().OfType<IMethodSymbol>().Any(TestAttributes.IsTest))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     How the walk recognizes the application: the assembly names its types carry, and the file names its process
    ///     starts under. Both compare ignoring case, so a match always counts toward starting, never away from it.
    /// </summary>
    private static (HashSet<string> Assemblies, HashSet<string> Files) IdentityOf(ProjectNode application, Solution solution)
    {
        var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { application.Name };
        foreach (var candidate in RepoWorkspace.ProjectsFor(solution, application))
            assemblies.Add(candidate.AssemblyName);
        foreach (var target in application.TargetPaths)
        {
            if (Path.GetFileNameWithoutExtension(target) is { Length: > 0 } name)
                assemblies.Add(name);
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in application.TargetPaths)
        {
            if (Path.GetFileName(target) is { Length: > 0 } file)
                files.Add(file);
        }

        foreach (var assembly in assemblies)
        {
            files.Add(assembly);
            files.Add(assembly + ".exe");
            files.Add(assembly + ".dll");
        }

        return (assemblies, files);
    }

    /// <summary>True when class prefix <paramref name="prefix"/> covers test name <paramref name="pattern"/>.</summary>
    private static bool Covers(string prefix, string pattern) =>
        prefix.EndsWith('.') && pattern.Length > prefix.Length && pattern.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>
    ///     One classification: the gathered types, and what they showed. The gathered types are the test class, its
    ///     bases, and every type of the test project reachable from them; a type outside the test project is a leaf
    ///     whose own name is still read, because a factory or a base class from another assembly still shows.
    /// </summary>
    private sealed class Walker
    {
        private readonly Compilation _compilation;
        private readonly Func<INamedTypeSymbol, bool> _isApplicationType;
        private readonly IReadOnlySet<string> _applicationFileNames;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<INamedTypeSymbol> _visited = new(SymbolEqualityComparer.Default);
        private readonly Queue<INamedTypeSymbol> _queue = new();
        private bool _starts;
        private bool _doubtful;

        public Walker(
            Compilation compilation,
            Func<INamedTypeSymbol, bool> isApplicationType,
            IReadOnlySet<string> applicationFileNames,
            CancellationToken cancellationToken)
        {
            _compilation = compilation;
            _isApplicationType = isApplicationType;
            _applicationFileNames = applicationFileNames;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Walks <paramref name="testClass"/> and answers what the walk showed.</summary>
        public Classification Classify(INamedTypeSymbol testClass)
        {
            if (!IsTestType(testClass))
                return Classification.CannotBeTold;
            Enqueue(testClass);
            while (_queue.Count > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Examine(_queue.Dequeue());
            }

            return _doubtful ? Classification.CannotBeTold : _starts ? Classification.Starts : Classification.DoesNot;
        }

        private void Examine(INamedTypeSymbol type)
        {
            ExamineBases(type);
            foreach (var contract in type.AllInterfaces)
                ConsiderType(contract);
            foreach (var declaration in type.DeclaringSyntaxReferences)
            {
                if (declaration.GetSyntax(_cancellationToken) is not BaseTypeDeclarationSyntax syntax)
                    continue;
                ExamineSyntax(_compilation.GetSemanticModel(syntax.SyntaxTree), syntax);
            }
        }

        /// <summary>
        ///     The base classes of <paramref name="type"/>: a test base joins the walk, an application base starts it
        ///     through the implicit base call, a framework base is left alone, and any other base from another assembly
        ///     cannot be told, unless it is a factory of the application.
        /// </summary>
        private void ExamineBases(INamedTypeSymbol type)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)
                    break;
                if (IsTestType(current) && !_isApplicationType(current))
                {
                    Enqueue(current);
                    continue;
                }

                if (_isApplicationType(current))
                {
                    _starts = true;
                    continue;
                }

                if (IsFrameworkType(current))
                    continue;
                if (!ExamineExternal(current))
                    _doubtful = true;
            }
        }

        /// <summary>One gathered declaration: every host shape in it, read from syntax bound to symbols.</summary>
        private void ExamineSyntax(SemanticModel model, BaseTypeDeclarationSyntax syntax)
        {
            foreach (var node in syntax.DescendantNodes())
            {
                switch (node)
                {
                    case GenericNameSyntax generic:
                        ExamineGeneric(model, generic);
                        break;
                    case ObjectCreationExpressionSyntax creation:
                        ExamineCreation(model, creation);
                        break;
                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        ExamineCreation(model, implicitCreation);
                        break;
                    case InvocationExpressionSyntax invocation:
                        ExamineInvocation(model, invocation);
                        break;
                    case AssignmentExpressionSyntax assignment:
                        ExamineAssignment(model, assignment);
                        break;
                    case IdentifierNameSyntax identifier:
                        ExamineIdentifier(model, identifier);
                        break;
                }
            }
        }

        /// <summary>
        ///     One generic name: a factory or host builder of an application type starts it, an application factory with
        ///     no application type cannot be told, and every other generic is followed for the types it names.
        /// </summary>
        private void ExamineGeneric(SemanticModel model, GenericNameSyntax generic)
        {
            switch (model.GetSymbolInfo(generic, _cancellationToken).Symbol)
            {
                case INamedTypeSymbol type:
                    if (IsHostType(type))
                    {
                        if (HasAppTypeArgument(type))
                            _starts = true;
                        else
                            _doubtful = true;
                    }
                    else if (IsFactoryName(type.Name) && HasAppTypeArgument(type))
                    {
                        _starts = true;
                    }

                    ConsiderType(type);
                    break;
                case IMethodSymbol method:
                    ConsiderType(method.ReturnType);
                    foreach (var argument in method.TypeArguments)
                        ConsiderType(argument);
                    break;
                default:
                    break;
            }
        }

        private void ExamineCreation(SemanticModel model, ObjectCreationExpressionSyntax creation) =>
            ExamineCreation(
                model,
                model.GetSymbolInfo(creation.Type, _cancellationToken).Symbol as INamedTypeSymbol,
                creation.ArgumentList,
                creation.Initializer);

        private void ExamineCreation(SemanticModel model, ImplicitObjectCreationExpressionSyntax creation) =>
            ExamineCreation(
                model,
                model.GetTypeInfo(creation, _cancellationToken).Type as INamedTypeSymbol,
                creation.ArgumentList,
                creation.Initializer);

        /// <summary>
        ///     One construction: a host type of the application starts it, and without an application type cannot be
        ///     told; a process start info names the process it starts; every other construction is followed for its type.
        /// </summary>
        private void ExamineCreation(SemanticModel model, INamedTypeSymbol? created, ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer)
        {
            if (created is null)
                return;
            if (IsHostType(created))
            {
                if (HasAppTypeArgument(created) || ArgumentsMentionApplication(model, arguments))
                    _starts = true;
                else
                    _doubtful = true;
                return;
            }

            if (IsProcessStartInfo(created))
            {
                // A bare construction names its file in a later assignment, which is read where it is written.
                if (arguments?.Arguments.Count > 0 || WritesFileName(initializer))
                    ExamineFileName(FileNameOf(model, arguments, initializer));
                return;
            }

            ConsiderType(created);
        }

        /// <summary>
        ///     One call: what it returns and what it is called on are followed, a process start names the process it
        ///     starts, an assembly or type loaded by name cannot be told, and a startup type of the application starts it.
        /// </summary>
        private void ExamineInvocation(SemanticModel model, InvocationExpressionSyntax invocation)
        {
            if (model.GetSymbolInfo(invocation, _cancellationToken).Symbol is not IMethodSymbol method)
                return;
            ConsiderType(method.ReturnType);
            ConsiderType(method.ContainingType);
            var name = method.ContainingType?.Name;
            var space = method.ContainingType?.ContainingNamespace?.ToDisplayString();
            if (name == "Process" && space == "System.Diagnostics" && method.Name == "Start")
                ExamineProcessStart(model, method, invocation.ArgumentList);
            else if (name == "Assembly" && space == "System.Reflection" && method.Name is "Load" or "LoadFrom" or "LoadFile")
                _doubtful = true;
            else if (name == "Type" && space == "System" && method is { IsStatic: true, Name: "GetType" })
                ExamineGetType(model, invocation.ArgumentList);
            else if (name == "Activator" && space == "System" && method.Name == "CreateInstance")
                ExamineCreateInstance(model, method, invocation.ArgumentList);
            else if (method.Name == "UseStartup" && StartupIsApplication(model, method, invocation.ArgumentList))
                _starts = true;
        }

        /// <summary>A file name assigned to a process start info: the application's starts it, and anything else cannot be told.</summary>
        private void ExamineAssignment(SemanticModel model, AssignmentExpressionSyntax assignment)
        {
            if (model.GetSymbolInfo(assignment.Left, _cancellationToken).Symbol is IPropertySymbol { Name: "FileName", ContainingType: INamedTypeSymbol container }
                && IsProcessStartInfo(container))
                ExamineFileName(ConstantOf(model, assignment.Right));
        }

        /// <summary>
        ///     One name: the application's entry type starts it, a type of the test project joins the walk, and the type
        ///     a name yields is followed, so a fixture behind a local or a call still shows.
        /// </summary>
        private void ExamineIdentifier(SemanticModel model, IdentifierNameSyntax identifier)
        {
            switch (model.GetSymbolInfo(identifier, _cancellationToken).Symbol)
            {
                case INamedTypeSymbol type:
                    ConsiderType(type);
                    break;
                case IMethodSymbol method:
                    ConsiderType(method.ContainingType);
                    break;
                case IFieldSymbol field:
                    ConsiderType(field.Type);
                    break;
                case IPropertySymbol property:
                    ConsiderType(property.Type);
                    break;
                case IEventSymbol @event:
                    ConsiderType(@event.Type);
                    break;
                case ILocalSymbol local:
                    ConsiderType(local.Type);
                    break;
                case IParameterSymbol parameter:
                    ConsiderType(parameter.Type);
                    break;
                default:
                    break;
            }

            if (ApplicationTypeOf(model, identifier) is { Name: "Program" })
                _starts = true;
        }

        /// <summary>A process start: the application's file starts it, <c>dotnet</c> with it starts it, and anything else cannot be told.</summary>
        private void ExamineProcessStart(SemanticModel model, IMethodSymbol method, ArgumentListSyntax? arguments)
        {
            if (arguments?.Arguments is not [var first, ..])
            {
                _doubtful = true;
                return;
            }

            // A start info names its file where it is constructed; a file name is read here.
            if (method.Parameters is [var parameter, ..] && parameter.Type is INamedTypeSymbol { Name: "ProcessStartInfo" })
            {
                if (first.Expression is ObjectCreationExpressionSyntax creation)
                    ExamineCreation(model, creation);
                else if (first.Expression is ImplicitObjectCreationExpressionSyntax implicitCreation)
                    ExamineCreation(model, implicitCreation);
                else
                    _doubtful = true;
                return;
            }

            ExamineFileName(DotnetFileName(model, arguments));
        }

        /// <summary>
        ///     A type loaded by name: a type of the test project stays classifiable and joins the walk, and anything else
        ///     cannot be told.
        /// </summary>
        private void ExamineGetType(SemanticModel model, ArgumentListSyntax? arguments)
        {
            if (arguments?.Arguments is [var first, ..]
                && ConstantOf(model, first.Expression) is { } name
                && _compilation.GetTypeByMetadataName(Unqualified(name)) is INamedTypeSymbol type
                && IsTestType(type)
                && !_isApplicationType(type))
            {
                Enqueue(type);
                return;
            }

            _doubtful = true;
        }

        /// <summary>
        ///     An instance the activator creates: a type the walk can resolve stays classifiable and is followed, and
        ///     anything else cannot be told.
        /// </summary>
        private void ExamineCreateInstance(SemanticModel model, IMethodSymbol method, ArgumentListSyntax? arguments)
        {
            if (method is { IsGenericMethod: true, TypeArguments: [INamedTypeSymbol generic] })
            {
                ConsiderType(generic);
                return;
            }

            if (arguments?.Arguments is [var first, ..]
                && first.Expression is TypeOfExpressionSyntax typeOf
                && model.GetSymbolInfo(typeOf.Type, _cancellationToken).Symbol is ITypeSymbol target)
            {
                ConsiderType(target);
                return;
            }

            _doubtful = true;
        }

        /// <summary>
        ///     Follows one referenced type: a type of the test project joins the walk, and a type from another assembly
        ///     is read for a host shape; a non-framework external type it cannot clear cannot be told, so the project
        ///     stays whole.
        /// </summary>
        private void ConsiderType(ITypeSymbol? type)
        {
            switch (type)
            {
                case INamedTypeSymbol named:
                    if (IsTestType(named) && !_isApplicationType(named))
                        Enqueue(named);
                    else if (!ExamineExternal(named) && !IsFrameworkType(named))
                        _doubtful = true;
                    foreach (var argument in named.TypeArguments)
                        ConsiderType(argument);
                    break;
                case IArrayTypeSymbol array:
                    ConsiderType(array.ElementType);
                    break;
                case IPointerTypeSymbol pointer:
                    ConsiderType(pointer.PointedAtType);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        ///     One type from another assembly: the application's entry type starts it, a factory or test server of the
        ///     application starts it, and an application factory with no application type cannot be told. True when the
        ///     type or a type it derives from showed one of those shapes.
        /// </summary>
        private bool ExamineExternal(INamedTypeSymbol type)
        {
            if (_isApplicationType(type))
            {
                if (type.Name == "Program")
                    _starts = true;
                return true;
            }

            for (var current = type; current is not null; current = current.BaseType)
            {
                if (IsHostType(current))
                {
                    if (HasAppTypeArgument(current))
                        _starts = true;
                    else
                        _doubtful = true;
                    return true;
                }

                if (IsFactoryName(current.Name) && HasAppTypeArgument(current))
                {
                    _starts = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when <paramref name="type"/> or a type it derives from names an application type argument.</summary>
        private bool HasAppTypeArgument(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.TypeArguments.OfType<INamedTypeSymbol>().Any(_isApplicationType))
                    return true;
            }

            return false;
        }

        /// <summary>True when an argument names a type of the application, as a test server's builder names its startup type.</summary>
        private bool ArgumentsMentionApplication(SemanticModel model, ArgumentListSyntax? arguments)
        {
            if (arguments is null)
                return false;
            foreach (var identifier in arguments.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (ApplicationTypeOf(model, identifier) is not null)
                    return true;
            }

            return false;
        }

        /// <summary>The application type <paramref name="identifier"/> names, or null.</summary>
        private INamedTypeSymbol? ApplicationTypeOf(SemanticModel model, IdentifierNameSyntax identifier) =>
            ApplicationTypeOf(model.GetSymbolInfo(identifier, _cancellationToken).Symbol);

        /// <summary>The application type <paramref name="symbol"/> is or belongs to, or null.</summary>
        private INamedTypeSymbol? ApplicationTypeOf(ISymbol? symbol) => symbol switch
        {
            INamedTypeSymbol type when _isApplicationType(type) => type,
            IMethodSymbol method when method.ContainingType is { } containing && _isApplicationType(containing) => containing,
            IFieldSymbol field when field.ContainingType is { } containing && _isApplicationType(containing) => containing,
            IPropertySymbol property when property.ContainingType is { } containing && _isApplicationType(containing) => containing,
            _ => null,
        };

        /// <summary>True when a startup hook names the application: a startup type argument, or an argument that names it.</summary>
        private bool StartupIsApplication(SemanticModel model, IMethodSymbol method, ArgumentListSyntax? arguments) =>
            method.TypeArguments.OfType<INamedTypeSymbol>().Any(_isApplicationType) || ArgumentsMentionApplication(model, arguments);

        /// <summary>A process file name: the application's starts it, and anything else, or no constant at all, cannot be told.</summary>
        private void ExamineFileName(string? file)
        {
            if (file is { Length: > 0 } name && (_applicationFileNames.Contains(name) || _applicationFileNames.Contains(Path.GetFileName(name))))
                _starts = true;
            else
                _doubtful = true;
        }

        /// <summary>The file a process start runs: through <c>dotnet</c>, the argument after it.</summary>
        private string? DotnetFileName(SemanticModel model, ArgumentListSyntax arguments)
        {
            var file = ConstantOf(model, arguments.Arguments[0].Expression);
            if (file is not null && IsDotnet(file) && arguments.Arguments.Count > 1)
                return ConstantOf(model, arguments.Arguments[1].Expression);
            return file;
        }

        /// <summary>The file name a process start info is constructed or initialized with, when it is a constant.</summary>
        private string? FileNameOf(SemanticModel model, ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer)
        {
            if (arguments?.Arguments is [var first, ..])
            {
                var file = ConstantOf(model, first.Expression);
                if (file is not null && IsDotnet(file) && arguments.Arguments.Count > 1)
                    return ConstantOf(model, arguments.Arguments[1].Expression);
                return file;
            }

            if (initializer is not null)
            {
                foreach (var assignment in initializer.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (assignment.Left is IdentifierNameSyntax { Identifier.Text: "FileName" })
                        return ConstantOf(model, assignment.Right);
                }
            }

            return null;
        }

        /// <summary>True when an initializer assigns the file name.</summary>
        private static bool WritesFileName(InitializerExpressionSyntax? initializer) =>
            initializer?.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.Left is IdentifierNameSyntax { Identifier.Text: "FileName" }) == true;

        /// <summary>The constant text of <paramref name="expression"/>, or null when it is not a constant string.</summary>
        private string? ConstantOf(SemanticModel model, ExpressionSyntax expression) =>
            model.GetConstantValue(expression, _cancellationToken).Value as string;

        /// <summary>True for the file names that run a .NET application through its host.</summary>
        private static bool IsDotnet(string file) =>
            Path.GetFileName(file) is { } name
            && (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase));

        /// <summary>The metadata name in a <c>Type.GetType</c> name, without its assembly part.</summary>
        private static string Unqualified(string name)
        {
            var assembly = name.IndexOf(',');
            return assembly < 0 ? name.Trim() : name[..assembly].Trim();
        }

        /// <summary>True for the two host types by their real names: the application factory and the test server.</summary>
        private static bool IsHostType(INamedTypeSymbol type) =>
            (type.Name, type.ContainingNamespace?.ToDisplayString()) switch
            {
                ("WebApplicationFactory", "Microsoft.AspNetCore.Mvc.Testing") => true,
                ("TestServer", "Microsoft.AspNetCore.TestHost") => true,
                _ => false,
            };

        /// <summary>True for a factory or host builder name, which carries the application in its type arguments.</summary>
        private static bool IsFactoryName(string name) =>
            name.EndsWith("Factory", StringComparison.Ordinal) || name.EndsWith("HostBuilder", StringComparison.Ordinal);

        private static bool IsProcessStartInfo(INamedTypeSymbol type) =>
            type.Name == "ProcessStartInfo" && type.ContainingNamespace?.ToDisplayString() == "System.Diagnostics";

        private bool IsTestType(INamedTypeSymbol type) =>
            SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly);

        /// <summary>True for a type of the framework itself, whose code cannot start the application.</summary>
        private static bool IsFrameworkType(INamedTypeSymbol type) =>
            type.ContainingAssembly?.Name is { } assembly
            && (assembly is "System.Private.CoreLib" or "mscorlib" or "System.Runtime" or "netstandard" or "System" or "System.Core"
                || assembly.StartsWith("System.", StringComparison.Ordinal));

        private void Enqueue(INamedTypeSymbol type)
        {
            // One definition's syntax is walked once, for the first instantiation queued; later ones differ only in
            // arguments, which are followed where they are written.
            if (_visited.Add(type.OriginalDefinition))
                _queue.Enqueue(type);
        }
    }
}
