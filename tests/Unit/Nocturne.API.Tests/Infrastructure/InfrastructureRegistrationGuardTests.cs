using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A class implemented in <c>Nocturne.Infrastructure.Data</c>, in any namespace, is registered by
/// that project's own registration methods and never by the API host, so its lifetime lives beside
/// the class and a host that wants it calls Infrastructure. A descriptor says what was registered
/// but not who registered it, so a registration pasted into the host in place of an Infrastructure
/// call leaves the same descriptors behind; this binds the API project's source instead.
/// </summary>
/// <remarks>
/// <para>
/// A registration call is one the semantic model binds to a method of the framework's registration
/// extensions (lifetimes, keys, descriptors, hosted services, HTTP clients and their handlers), to
/// <c>ServiceDescriptor</c>'s constructor or factories, or to <c>Add</c> and <c>Insert</c> on a
/// descriptor list. It fails on a non-abstract Infrastructure class among the call's type
/// arguments (written or inferred), among the types of its arguments and of every expression
/// inside them, in a <c>typeof</c>, or as the return type of a lambda or a method group. An
/// Infrastructure interface or static class is allowed.
/// </para>
/// <para>
/// Source generators do not run, so a call whose bound method or type argument is missing is
/// reported rather than passed over.
/// </para>
/// <para>
/// It does not see a class reached by a value the model cannot type: a <c>Type</c> held in a
/// local and passed on (<c>AddScoped(serviceType, implementationType)</c>), a method group whose
/// body builds a class but returns an interface, or a registration made by a host helper that is
/// not one of the framework's. <c>InfrastructureClassRegistrationGuardTests</c> covers those from
/// the descriptors the host's real startup produced, but only against what Infrastructure also
/// registers: such a registration that replaces an Infrastructure call with the same descriptor
/// is seen by neither half.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class InfrastructureRegistrationGuardTests
{
    private const string InfrastructureAssembly = "Nocturne.Infrastructure.Data";

    private const string Descriptor = "Microsoft.Extensions.DependencyInjection.ServiceDescriptor";

    private static readonly HashSet<string> RegistrationHolders =
    [
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions",
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions",
        "Microsoft.Extensions.DependencyInjection.HttpClientBuilderExtensions",
        Descriptor,
    ];

    private static readonly HashSet<string> DescriptorLists =
    [
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IList<T>",
    ];

    private static readonly HashSet<string> RegistrationSurfaces =
    [
        "Microsoft.Extensions.DependencyInjection.IServiceCollection",
        "Microsoft.Extensions.DependencyInjection.IHttpClientBuilder",
        Descriptor,
    ];

    private const string Support = """
        global using Microsoft.Extensions.DependencyInjection.Extensions;
        global using Microsoft.Extensions.Logging;
        global using Nocturne.Core.Contracts.Events;
        global using Nocturne.Core.Contracts.Infrastructure;
        global using Nocturne.Infrastructure.Data;
        global using Nocturne.Infrastructure.Data.Abstractions;
        global using Nocturne.Infrastructure.Data.Configuration;
        global using Nocturne.Infrastructure.Data.Extensions;
        global using Nocturne.Infrastructure.Data.Repositories;
        global using Nocturne.Infrastructure.Data.Repositories.V4;
        global using Nocturne.Infrastructure.Data.Services;
        global using Nocturne.Infrastructure.Data.Services.Deduplication;

        class HostLinker : IDuplicateLinker
        {
            public Task<Nocturne.Core.Models.DeduplicationBatchResult> DeduplicateBatchAsync(
                Nocturne.Core.Models.RecordType recordType,
                IReadOnlyList<Nocturne.Core.Models.DeduplicationInput> records,
                CancellationToken cancellationToken = default) => null!;
        }
        """;

    [Fact]
    public void TheApiHostRegistersNoClassInfrastructureDataImplements()
    {
        var scan = Scan(RepoCompilation.Api);

        scan.Calls.Should().BeGreaterThan(0, "the scan reads the host's registrations");
        scan.Unbound.Should().BeEmpty("every call that may register a class must bind, or the scan proves nothing about it");
        scan.Registered.Select(r => $"{r.Type} ({r.Method}) at {r.Where}").Should().BeEmpty(
            "Infrastructure registers the classes it implements, beside the class; the host calls its extension method");
    }

    [Theory]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDiscrepancyAnalysisRepository, DiscrepancyAnalysisRepository>(); }", "DiscrepancyAnalysisRepository")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddSingleton<DeduplicationAdmin>(); }", "DeduplicationAdmin")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAddTransient<IDeduplicationAdmin, DeduplicationAdmin>(); }", "DeduplicationAdmin")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped(typeof(IDuplicateLinker), typeof(DuplicateLinker)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => new DuplicateLinker(null!, null!)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => sp.GetRequiredService<DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddKeyedScoped<IDuplicateLinker, DuplicateLinker>(\"k\"); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(ServiceDescriptor.Scoped<IDuplicateLinker, DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Replace(ServiceDescriptor.Describe(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new ServiceDescriptor(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAddEnumerable(ServiceDescriptor.Scoped<IRecordChangePublisher, RecordChangePublisher>()); }", "RecordChangePublisher")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddHostedService<DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddHttpClient<IDuplicateLinker, DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IHttpClientBuilder b) => b.AddHttpMessageHandler<DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) { var d = ServiceDescriptor.Singleton<IDuplicateLinker, DuplicateLinker>(); s.Add(d); } }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAdd(new ServiceDescriptor(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Replace(new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Insert(0, new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped<IDuplicateLinker, DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("using Linker = Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateLinker; class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Linker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s, PostgreSqlConfiguration postgreSqlOptions) => s.AddSingleton(postgreSqlOptions); }", "PostgreSqlConfiguration")]
    [InlineData("class C { void M(IServiceCollection s) { var linker = new DuplicateLinker(null!, null!); s.AddSingleton<IDuplicateLinker>(linker); } }", "DuplicateLinker")]
    [InlineData("class C { static DuplicateLinker Make(IServiceProvider sp) => null!; void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(Make); }", "DuplicateLinker")]
    [InlineData("class C { static DuplicateLinker Make(IServiceProvider sp) => null!; void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(C.Make); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) { DuplicateLinker Make(IServiceProvider sp) => null!; s.AddScoped<IDuplicateLinker>(Make); } }", "DuplicateLinker")]
    public void SeesAnInfrastructureClassRegisteredByTheHost(string source, string expected)
    {
        var scan = ScanSnippet(source);

        scan.Unbound.Should().BeEmpty();
        scan.Registered.Select(r => r.Type).Should().Contain(expected);
    }

    [Theory]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, HostLinker>(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDataServices(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDiscrepancyAnalysisRepository(); }")]
    [InlineData("class C { void M(IServiceCollection s, PostgreSqlConfiguration options) => s.AddPostgreSqlConfiguration(options); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.Configure<DuplicateLinker>(o => { }); }")]
    [InlineData("class C { void M() => new List<DuplicateLinker>().Add(null!); }")]
    [InlineData("class C { void M(List<DuplicateLinker> l) => l.Add(new(null!, null!)); }")]
    [InlineData("using Linker = HostLinker; class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Linker>(); }")]
    [InlineData("class C { static IDiscrepancyAnalysisRepository Make(IServiceProvider sp) => null!; void M(IServiceCollection s) => s.AddScoped<IDiscrepancyAnalysisRepository>(Make); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => { TenantJobScope.Open(sp, null, \"job\"); return new HostLinker(); }); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => (IDuplicateLinker)sp.GetRequiredService(typeof(TenantJobScope))); }")]
    [InlineData("class C { void M(IServiceCollection s, Type implementation) => s.AddScoped(typeof(IDuplicateLinker), implementation); }")]
    public void IgnoresWhatTheHostMayDo(string source)
    {
        var scan = ScanSnippet(source);

        scan.Unbound.Should().BeEmpty();
        scan.Registered.Should().BeEmpty();
    }

    [Theory]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, NotDeclared>(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScopedAgain<IDuplicateLinker, HostLinker>(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new ServiceDescriptor(typeof(IDuplicateLinker), NotDeclared.Make, ServiceLifetime.Scoped)); }")]
    [InlineData("class C { void M(IServiceCollection s) => ServiceDescriptor.Scooped<IDuplicateLinker, HostLinker>(); }")]
    public void FailsACallItCannotBind(string source) =>
        ScanSnippet(source).Unbound.Should().NotBeEmpty();

    private sealed record Registered(string Type, string Method, string Where);

    private sealed record ScanResult(int Calls, IReadOnlyList<string> Unbound, IReadOnlyList<Registered> Registered);

    private static ScanResult ScanSnippet(string source) =>
        Scan(RepoCompilation.OfSnippet(source, Support));

    private static ScanResult Scan(CSharpCompilation compilation)
    {
        var scans = compilation.SyntaxTrees.AsParallel().AsOrdered()
            .Select(tree => Scan(compilation, tree))
            .ToList();

        return new ScanResult(
            scans.Sum(scan => scan.Calls),
            [.. scans.SelectMany(scan => scan.Unbound).Distinct()],
            [.. scans.SelectMany(scan => scan.Registered).Distinct()]);
    }

    private static ScanResult Scan(CSharpCompilation compilation, SyntaxTree tree)
    {
        var calls = 0;
        var unbound = new List<string>();
        var registered = new List<Registered>();
        var model = compilation.GetSemanticModel(tree);

        foreach (var node in tree.GetRoot().DescendantNodes().Where(IsCall))
        {
            var where = $"{tree.FilePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
            var info = model.GetSymbolInfo(node);
            var method = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault(IsRegistration);

            if (method is null)
            {
                if (TouchesRegistrationSurface(model, node))
                    unbound.Add($"{where} {Shorten(node)}");
                continue;
            }

            if (!IsRegistration(method))
                continue;

            calls++;
            var types = TypesOf(model, node, method).ToList();
            if (types.Any(type => type.TypeKind == TypeKind.Error))
                unbound.Add($"{where} {Shorten(node)}");

            registered.AddRange(types
                .Where(IsInfrastructureClass)
                .Select(type => new Registered(type.Name, method.Name, where)));
        }

        return new ScanResult(calls, unbound, registered);
    }

    private static bool IsCall(SyntaxNode node) =>
        node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax;

    private static string Shorten(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 100 ? text : text[..100] + "...";
    }

    private static string Display(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString();

    private static bool IsRegistration(IMethodSymbol method)
    {
        var holder = method.ContainingType;

        return RegistrationHolders.Contains(Display(holder))
            || (DescriptorLists.Contains(Display(holder)) && holder.TypeArguments is [{ } element] && Display(element) == Descriptor);
    }

    private static bool TouchesRegistrationSurface(SemanticModel model, SyntaxNode node)
    {
        var type = node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } => model.GetTypeInfo(member.Expression).Type,
            BaseObjectCreationExpressionSyntax => model.GetTypeInfo(node).Type,
            _ => null,
        };

        return type is { TypeKind: TypeKind.Error }
            || (type is not null && type.AllInterfaces.Prepend(type).Any(candidate => RegistrationSurfaces.Contains(Display(candidate))));
    }

    private static IEnumerable<ITypeSymbol> TypesOf(SemanticModel model, SyntaxNode call, IMethodSymbol method)
    {
        var arguments = call switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
            BaseObjectCreationExpressionSyntax { ArgumentList: { } list } => list.Arguments,
            _ => [],
        };

        var nodes = arguments.SelectMany(argument => argument.Expression.DescendantNodesAndSelf())
            .Where(node => node is (ExpressionSyntax or TypeSyntax) and not OmittedArraySizeExpressionSyntax);

        var roots = method.TypeArguments.Concat(nodes.SelectMany(node =>
        {
            var info = model.GetTypeInfo(node);
            var group = model.GetSymbolInfo(node).Symbol is IMethodSymbol named
                && node.Parent is not InvocationExpressionSyntax
                ? named.ReturnType
                : null;
            return new[] { info.Type, info.ConvertedType, group }.OfType<ITypeSymbol>();
        }));

        return roots.SelectMany(Walk);
    }

    private static IEnumerable<ITypeSymbol> Walk(ITypeSymbol type)
    {
        yield return type;

        var inner = type switch
        {
            INamedTypeSymbol named => named.TypeArguments,
            IArrayTypeSymbol array => [array.ElementType],
            _ => [],
        };

        foreach (var argument in inner)
            foreach (var nested in Walk(argument))
                yield return nested;
    }

    private static bool IsInfrastructureClass(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsStatic: false } named
        && named.ContainingAssembly?.Name == InfrastructureAssembly;
}
