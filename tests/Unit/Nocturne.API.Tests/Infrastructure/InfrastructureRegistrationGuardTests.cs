using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A class implemented in <c>Nocturne.Infrastructure.Data</c>, in any namespace, is registered by
/// that project's own registration methods and never by the API host, so its lifetime lives beside
/// the class and a host that wants it calls Infrastructure. A descriptor says what was registered
/// but not who registered it, so a registration pasted into the host in place of an Infrastructure
/// call leaves the same descriptors behind; this reads the API project's source instead.
/// </summary>
/// <remarks>
/// <para>
/// This half sees a registration written in the API source that names the class: in a type
/// argument, a <c>typeof</c>, a factory lambda, a <c>ServiceDescriptor</c> (built, described or
/// target-typed into <c>Add</c>, <c>TryAdd</c>, <c>Replace</c> or <c>Insert</c>), or through a
/// <c>using X = ...</c> alias declared in the same file.
/// </para>
/// <para>
/// It does not see a class reached without its name in the call: an instance held in a local, a
/// method group, a <c>typeof</c> kept in a local, or an alias declared in another file.
/// <c>InfrastructureClassRegistrationGuardTests</c> covers those from the descriptors the host's
/// real startup produced, but only against what Infrastructure also registers: a host registration
/// that replaces an Infrastructure call with an instance or method group of the same service type,
/// lifetime and implementation is seen by neither half.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public partial class InfrastructureRegistrationGuardTests
{
    private const string DescriptorType = "ServiceDescriptor";

    private static readonly Regex RegistrationMethod = RegistrationMethodPattern();

    private static readonly Regex DescriptorMethod = DescriptorMethodPattern();

    private static readonly HashSet<string> InfrastructureClasses = typeof(NocturneDbContext).Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false }
            && !type.Name.StartsWith('<'))
        .Select(type => type.Name.Split('`')[0])
        .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TheApiHostRegistersNoClassInfrastructureDataImplements()
    {
        var registrations = RepoSources.Under("src/API")
            .SelectMany(file => Registrations(file.Path, file.Tree))
            .ToList();
        registrations.Should().NotBeEmpty("the scan reads the host's registrations");

        registrations
            .Where(r => InfrastructureClasses.Contains(r.Type))
            .Select(r => $"{r.Type} ({r.Method}) at {r.Path}:{r.Line}")
            .Distinct()
            .Should().BeEmpty("Infrastructure registers the classes it implements, beside the class; the host calls its extension method");
    }

    [Fact]
    public void TheScanMatchesClassNamesAndNeverInterfaceNames()
    {
        InfrastructureClasses.Should().Contain(
            ["DuplicateLinker", "DeduplicationAdmin", "DiscrepancyAnalysisRepository", "AlertRepository", "NocturneDbContextFactory", "PostgreSqlConfiguration"]);
        InfrastructureClasses.Should().NotContain(
            ["IDuplicateLinker", "IDiscrepancyAnalysisRepository", "ServiceCollectionExtensions"]);
    }

    [Theory]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDiscrepancyAnalysisRepository, DiscrepancyAnalysisRepository>(); }", "DiscrepancyAnalysisRepository")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddSingleton<DeduplicationAdmin>(); }", "DeduplicationAdmin")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAddTransient<IDeduplicationAdmin, DeduplicationAdmin>(); }", "DeduplicationAdmin")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped(typeof(IDuplicateLinker), typeof(DuplicateLinker)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => new DuplicateLinker(sp.GetRequiredService<ILogger>())); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker>(sp => sp.GetRequiredService<DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddKeyedScoped<IDuplicateLinker, DuplicateLinker>(\"k\"); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(ServiceDescriptor.Scoped<IDuplicateLinker, DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Replace(ServiceDescriptor.Describe(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new ServiceDescriptor(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAddEnumerable(ServiceDescriptor.Scoped<IRecordChangeSubscriber, RecordChangePublisher>()); }", "RecordChangePublisher")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddHostedService<DuplicateLinker>(); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) { var d = ServiceDescriptor.Singleton<IDuplicateLinker, DuplicateLinker>(); s.Add(d); } }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.TryAdd(new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Replace(new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Insert(0, new(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(typeof(IDuplicateLinker), typeof(DuplicateLinker), ServiceLifetime.Scoped)); }", "DuplicateLinker")]
    [InlineData("class C { void M(IServiceCollection s) => s.Add(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Scoped<IDuplicateLinker, DuplicateLinker>()); }", "DuplicateLinker")]
    [InlineData("using Linker = Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateLinker; class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Linker>(); }", "DuplicateLinker")]
    public void SeesAnInfrastructureClassRegisteredByTheHost(string source, string expected) =>
        Registrations("", CSharpSyntaxTree.ParseText(source)).Select(r => r.Type)
            .Where(InfrastructureClasses.Contains).Should().Contain(expected);

    [Theory]
    [InlineData("class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, HostLinker>(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDataServices(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddDiscrepancyAnalysisRepository(); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.AddPostgreSqlConfiguration(options); }")]
    [InlineData("class C { void M(IServiceCollection s) => s.Configure<DuplicateLinker>(o => { }); }")]
    [InlineData("class C { void M() => new List<DuplicateLinker>().Add(null); }")]
    [InlineData("class C { void M(List<DuplicateLinker> l) => l.Add(new(1)); }")]
    [InlineData("using Linker = HostLinker; class C { void M(IServiceCollection s) => s.AddScoped<IDuplicateLinker, Linker>(); }")]
    public void IgnoresWhatTheHostMayDo(string source) =>
        Registrations("", CSharpSyntaxTree.ParseText(source)).Select(r => r.Type)
            .Where(InfrastructureClasses.Contains).Should().BeEmpty();

    private sealed record Registration(string Path, int Line, string Method, string Type);

    private static IEnumerable<Registration> Registrations(string path, SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(directive => directive.Alias is not null && directive.NamespaceOrType is not null)
            .ToDictionary(
                directive => directive.Alias!.Name.Identifier.ValueText,
                directive => RightmostName(directive.NamespaceOrType!),
                StringComparer.Ordinal);

        return root.DescendantNodes()
            .Select(node => (Node: node, Scope: RegistrationScope(node)))
            .Where(registration => registration.Scope is not null)
            .SelectMany(registration => registration.Scope!.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
                .Select(name => name.Identifier.ValueText)
                .Select(name => new Registration(
                    path,
                    registration.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    registration.Node is InvocationExpressionSyntax call ? InvokedName(call) : "new ServiceDescriptor",
                    aliases.GetValueOrDefault(name, name))));
    }

    private static SyntaxNode? RegistrationScope(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax call when RegistrationMethod.IsMatch(InvokedName(call)) || IsDescriptorHelper(call) => call,
        InvocationExpressionSyntax call when DescriptorMethod.IsMatch(InvokedName(call))
            && call.ArgumentList.Arguments.Any(argument => argument.Expression is ImplicitObjectCreationExpressionSyntax) => call.ArgumentList,
        ObjectCreationExpressionSyntax creation when RightmostName(creation.Type) == DescriptorType => creation,
        _ => null,
    };

    private static bool IsDescriptorHelper(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax member && RightmostName(member.Expression) == DescriptorType;

    private static string RightmostName(SyntaxNode node) => node switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => "",
    };

    private static string InvokedName(InvocationExpressionSyntax call) => RightmostName(call.Expression);

    [GeneratedRegex(@"^(?:(?:Try)?Add(?:Keyed)?(?:Scoped|Singleton|Transient)|TryAddEnumerable|AddHostedService|AddHttpClient)$")]
    private static partial Regex RegistrationMethodPattern();

    [GeneratedRegex(@"^(?:Add|TryAdd|Replace|Insert)$")]
    private static partial Regex DescriptorMethodPattern();
}
