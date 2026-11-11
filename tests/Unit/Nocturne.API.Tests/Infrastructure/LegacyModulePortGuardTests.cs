using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Extensions;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// A legacy concept's store, cache and decomposer are collaborators of its module
/// (<c>Services/Entries</c>, <c>Services/Treatments</c>, <c>Services/DeviceStatuses</c>, and the
/// concept's decomposer in <c>Services/V4</c>): outside the module, its own tests and
/// <see cref="ServiceRegistrationExtensions"/>, no code names a type the module declares, whose static
/// members are as reachable as the ports. Consumers use the module's service port and their tests mock
/// it. <c>internal</c> cannot say this inside one assembly.
/// </summary>
/// <remarks>
/// <para>
/// The rule is a name, not a list of leak shapes: any identifier in code that names a type the module
/// declares counts, however it is injected, resolved, aliased or passed. The names are read from the
/// module's source folders, so a type added to one is guarded with no edit here. A nested type is
/// reached only through its declaring type, so only top-level types are named. Doc comments, comments
/// and string literals do not count, so a type resolved from a string is not seen. A call to an extension
/// method never writes its declaring type, so a module declares none, and a type of a module's name
/// declared outside it, a partial part included, is refused. Constructor
/// reflection over the API and test assemblies is the second net; it places each type by the file that declares it,
/// not by its namespace.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class LegacyModulePortGuardTests
{
    private sealed record Module(string Folder, string Concept, string[] Ports)
    {
        public bool Owns(string path) =>
            path.StartsWith($"src/API/Nocturne.API/Services/{Folder}/", StringComparison.Ordinal)
            || path.StartsWith($"src/API/Nocturne.API/Services/V4/{Concept}Decomposer", StringComparison.Ordinal)
            || path.StartsWith($"tests/Unit/Nocturne.API.Tests/Services/{Folder}/", StringComparison.Ordinal)
            || path.StartsWith($"tests/Unit/Nocturne.API.Tests/Services/V4/{Concept}Decomposer", StringComparison.Ordinal);

        public bool Declares(string path) => Owns(path) && path.StartsWith("src/", StringComparison.Ordinal);
    }

    private static readonly Module[] Modules =
    [
        new("Entries", "Entry", ["IEntryStore", "IEntryCache", "IEntryDecomposer"]),
        new("Treatments", "Treatment", ["ITreatmentStore", "ITreatmentCache", "ITreatmentDecomposer"]),
        new("DeviceStatuses", "DeviceStatus", ["IDeviceStatusStore", "IDeviceStatusDecomposer"]),
    ];

    private const string Registration = "src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs";

    private static readonly string[] ReflectedSources = ["src/API/Nocturne.API", "tests/Unit/Nocturne.API.Tests"];

    private static readonly IReadOnlyDictionary<string, Module> ModuleOfPort =
        Modules.SelectMany(m => m.Ports.Select(p => (p, m))).ToDictionary(x => x.p, x => x.m);

    private static readonly IReadOnlyList<(string Name, Module Module)> Declared =
        Modules.SelectMany(m => RepoSources.Src
                .Where(source => m.Declares(source.Path))
                .SelectMany(source => TopLevelTypeNames(source.Tree))
                .Select(name => (name, m)))
            .Distinct()
            .ToList();

    private static readonly IReadOnlyDictionary<string, Module> ModuleOfName =
        Declared.GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First().Module);

    /// <summary>
    /// A reference outside a module to a type it declares, with the reason: one member of a source file, or
    /// one test file. Each is a consumer reaching into the module and is to leave this list by going through
    /// the module's service, not to be joined by another.
    /// </summary>
    private sealed record Exemption(string Path, string? Member, string Name, string Reason);

    private static Exemption InMember(string path, string member, string name, string reason) =>
        new(path, member, name, reason);

    private static Exemption InTest(string path, string name, string reason) =>
        new(path, null, name, reason);

    private const string BuildsDecomposer = "builds the decomposer over fake repositories to test its writes";

    private const string CallsStaticMapper = "calls a pure static mapper of the decomposer";

    private static readonly Exemption[] Exemptions =
    [
        InMember("src/API/Nocturne.API/Services/ConnectorPublishing/TreatmentPublisher.cs", "TreatmentPublisher.PublishTreatmentsAsync", "TreatmentDecomposer", "calls the decomposer's static upstream fingerprint"),
        InMember("src/API/Nocturne.API/Services/Caching/LegacyReadCacheInvalidation.cs", "LegacyReadCacheInvalidation.FamiliesByModel", "LegacyTreatmentTables", "lists the models the treatment tables serve, which no module service exposes"),
        InTest("tests/Integration/Nocturne.API.Tests/TreatmentBatchFailureIntegrationTests.cs", "TreatmentReadService", "builds the store over a decomposer whose note repository fails, to test its batch writes"),
        InTest("tests/Integration/Nocturne.API.Tests/Migration/AddProfileActivationsMigrationTests.cs", "LegacyTreatmentTables", "compares the treatments a migrated activation is served as with the expected ones"),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Caching/LegacyReadCacheInvalidationTests.cs", "LegacyTreatmentTables", "checks the invalidation covers every model the treatment tables serve"),
        InTest("tests/Unit/Nocturne.API.Tests/Services/ProfileActivations/NativeProfileSwitchesTests.cs", "LegacyProfileActivationTable", "reads the rows the profile activation table serves as treatments"),
        InTest("tests/Unit/Nocturne.API.Tests/Services/ProfileActivations/ProfileActivationLegacyFieldsTests.cs", "LegacyTreatmentTables", "projects an activation through the treatment table that serves it"),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Realtime/StorageDeleteBroadcastContractTests.cs", "SignalRTreatmentEventSink", "builds the treatment event sink to test what a delete broadcasts"),
        InTest("tests/Integration/Nocturne.API.Tests/TreatmentBatchFailureIntegrationTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Controllers/V1/TreatmentsPostUpsertTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/ConnectorPublishing/ClientIdRekeyTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/ConnectorPublishing/RecentTreatmentRepublishTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/ConnectorPublishing/RecentGlucoseAndDeviceStatusPublishTests.cs", "DeviceStatusDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/TestDoubles/DeviceStatusDecomposerDoubles.cs", "DeviceStatusDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/BatchResendMatchesSingleTests.cs", "EntryDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/BatchResendMatchesSingleTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/BatchResendMatchesSingleTests.cs", "DeviceStatusDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/ResendKeepsCorrelationIdTests.cs", "EntryDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/ResendKeepsCorrelationIdTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/PreBolusWriteBackPullBackTests.cs", "TreatmentDecomposer", BuildsDecomposer),
        InTest("tests/Unit/Nocturne.API.Tests/Services/V4/TreatmentRequiredColumnsTests.cs", "TreatmentDecomposer", CallsStaticMapper),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Analytics/ChartDataServiceTests.cs", "TreatmentDecomposer", CallsStaticMapper),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Analytics/LoopAnalysisTempBasalCancelTests.cs", "TreatmentDecomposer", CallsStaticMapper),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Seeding/DemoLoopAnalysisTests.cs", "TreatmentDecomposer", CallsStaticMapper),
        InTest("tests/Unit/Nocturne.API.Tests/Services/Seeding/DemoLoopHealthTests.cs", "TreatmentDecomposer", CallsStaticMapper),
    ];

    private static bool IsExempt(string path, string member, string name) =>
        Exemptions.Any(e => e.Path == path && e.Name == name && (e.Member is null || e.Member == member));

    private static readonly Assembly Api = typeof(ServiceRegistrationExtensions).Assembly;

    [Fact]
    public void NothingTakesAPortInAConstructorOutsideItsModule()
    {
        var files = DeclaringFiles();
        var takers = ConstructorTakers(Api, files)
            .Concat(ConstructorTakers(typeof(LegacyModulePortGuardTests).Assembly, files));

        takers.Should().BeEmpty("inject the module's service, not its store, cache or decomposer");
    }

    [Fact]
    public void NothingNamesAModuleInternalInSourceOutsideItsModule()
    {
        var namers = Sources("src")
            .Where(s => s.Path != Registration)
            .SelectMany(s => NamedModuleTypes(s.Tree)
                .Where(hit => !ModuleOfName[hit.Name].Owns(s.Path) && !IsExempt(s.Path, hit.Member, hit.Name))
                .Select(hit => $"{s.Path}: {hit.Member}({hit.Name})"))
            .Distinct();

        string.Join(Environment.NewLine, namers).Should().BeEmpty("use the module's service, not its store, cache, decomposer or concrete classes");
    }

    [Fact]
    public void NothingNamesAModuleInternalInATestOutsideItsModule()
    {
        var namers = Sources("tests")
            .SelectMany(s => NamedModuleTypes(s.Tree)
                .Where(hit => !ModuleOfName[hit.Name].Owns(s.Path) && !IsExempt(s.Path, hit.Member, hit.Name))
                .Select(hit => $"{s.Path}({hit.Name})"))
            .Distinct();

        string.Join(Environment.NewLine, namers).Should().BeEmpty("a consumer's test mocks the module's service");
    }

    [Fact]
    public void EveryExemptionStillNamesItsType()
    {
        var sources = Sources("src").Concat(Sources("tests")).ToDictionary(s => s.Path);

        var stale = Exemptions
            .Where(e => !sources.TryGetValue(e.Path, out var source)
                || !NamedModuleTypes(source.Tree).Any(hit => hit.Name == e.Name && (e.Member is null || hit.Member == e.Member)))
            .Select(e => $"{e.Path}({e.Member}, {e.Name})");

        stale.Should().BeEmpty("an exemption outlives the reference it excuses");
        Exemptions.Should().OnlyContain(e => ModuleOfName.ContainsKey(e.Name) && !ModuleOfPort.ContainsKey(e.Name), "only a type that is not a port is excusable");
        Exemptions.Should().OnlyContain(e => !ModuleOfName[e.Name].Owns(e.Path), "a file inside its module needs no exemption");
        Exemptions.Where(e => e.Path.StartsWith("src/", StringComparison.Ordinal) != (e.Member != null))
            .Should().BeEmpty("source is excused by member, a test by file");
        Exemptions.Select(e => (e.Path, e.Member, e.Name)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryModuleTypeBelongsToOneModule()
    {
        Declared.GroupBy(d => d.Name).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(d => d.Module.Folder))}")
            .Should().BeEmpty("a name read from two modules cannot say which one a hit leaks");

        foreach (var module in Modules)
            module.Ports.Where(port => !Declared.Contains((port, module)))
                .Should().BeEmpty("a port is declared in its own module's folder");
    }

    [Fact]
    public void NoModuleDeclaresAnExtensionMethod()
    {
        ModuleExtensionMethods(RepoSources.Src).Should().BeEmpty(
            "a call to an extension method never writes its declaring type, so the name scan cannot see it");
    }

    [Fact]
    public void NoModuleTypeIsDeclaredOutsideItsModule()
    {
        OutsideDeclarations(RepoSources.Src.Concat(RepoSources.Tests)).Should().BeEmpty(
            "a partial part outside the module reads its private members without naming it, and another type of the same name makes every hit ambiguous");
    }

    [Fact]
    public void EveryPortIsDeclaredOnceAndInternal()
    {
        var contracts = Assembly.Load("Nocturne.Core.Contracts");
        var declared = Api.GetTypes().Concat(contracts.GetTypes())
            .Where(t => t.IsInterface && ModuleOfPort.ContainsKey(t.Name))
            .ToList();

        declared.Select(t => t.Name).Should().BeEquivalentTo(ModuleOfPort.Keys, "a renamed port would drop out of the guard");
        declared.Should().OnlyContain(t => !t.IsPublic && t.Assembly == Api);
    }

    [Fact]
    public void DerivesItsNamesFromTheTypesTheModuleFoldersDeclare()
    {
        ModuleOfName.Keys.Should().Contain(["PreBolusSplit", "CarbFoodIndex", "StoredEntryMatch", "TreatmentDecomposer", "EntryService", "DeviceStatusService"]);
        ModuleOfName.Keys.Should().NotContain(["LegacyId", "IobCobReader", "BolusWizardService"], "a nested type is named through its declaring type, and these are not a module's");
    }

    [Theory]
    [InlineData("static class E { public static int Twice(this int x) => x; static int Plain(int x) => x; }", "E.Twice")]
    [InlineData("static class E { extension(int x) { public int Twice() => x; public int Three => 3; } }", "E.Twice")]
    [InlineData("static class E { extension<T>(List<T> list) { public static List<T> Empty() => []; } }", "E.Empty")]
    [InlineData("static class E { static int Plain(int x) => x; } class C { int M(int x) => x; }", "")]
    public void ReadsTheExtensionMethodsOfASource(string source, string expected) =>
        string.Join(",", ExtensionMethods(CSharpSyntaxTree.ParseText(source))).Should().Be(expected);

    [Theory]
    [InlineData("src/API/Nocturne.API/Services/Treatments/X.cs", "static class X { static int Twice(this int x) => x; }", "src/API/Nocturne.API/Services/Treatments/X.cs: X.Twice")]
    [InlineData("src/API/Nocturne.API/Services/Treatments/X.cs", "static class X { static int Plain(int x) => x; }", "")]
    [InlineData("src/API/Nocturne.API/Helpers/X.cs", "static class X { static int Twice(this int x) => x; }", "")]
    [InlineData("tests/Unit/Nocturne.API.Tests/Services/Treatments/X.cs", "static class X { static int Twice(this int x) => x; }", "")]
    public void FindsAnExtensionMethodAModuleFolderDeclares(string path, string source, string expected) =>
        string.Join(",", ModuleExtensionMethods([new SourceFile(path, source)]))
            .Should().Be(expected);

    [Theory]
    [InlineData("src/API/Nocturne.API/Helpers/X.cs", "partial class LegacyTreatmentTables { }", "src/API/Nocturne.API/Helpers/X.cs: LegacyTreatmentTables")]
    [InlineData("src/API/Nocturne.API/Helpers/X.cs", "namespace N { class EntryService { } }", "src/API/Nocturne.API/Helpers/X.cs: EntryService")]
    [InlineData("tests/Unit/Nocturne.API.Tests/Helpers/X.cs", "enum PreBolusSplit { }", "tests/Unit/Nocturne.API.Tests/Helpers/X.cs: PreBolusSplit")]
    [InlineData("src/API/Nocturne.API/Services/Treatments/X.cs", "partial class LegacyTreatmentTables { }", "")]
    [InlineData("src/API/Nocturne.API/Services/Entries/X.cs", "partial class LegacyTreatmentTables { }", "src/API/Nocturne.API/Services/Entries/X.cs: LegacyTreatmentTables")]
    [InlineData("src/API/Nocturne.API/Helpers/X.cs", "class Unrelated { class EntryService { } }", "")]
    [InlineData("tests/Unit/Nocturne.API.Tests/Services/Treatments/X.cs", "class LegacyTreatmentTables { }", "")]
    public void FindsAModuleTypeDeclaredOutsideItsModule(string path, string source, string expected) =>
        string.Join(",", OutsideDeclarations([new SourceFile(path, source)]))
            .Should().Be(expected);

    [Theory]
    [InlineData("namespace N; public static class A { public class Nested { } } internal delegate void D(); enum E { } record struct R(int X);", "A,D,E,R")]
    [InlineData("class Outer { delegate void Inner(); interface IInner { } }", "Outer")]
    public void ReadsTopLevelTypesOfASource(string source, string expected) =>
        string.Join(",", TopLevelTypeNames(CSharpSyntaxTree.ParseText(source))).Should().Be(expected);

    [Theory]
    [InlineData("Nocturne.API.Services.Entries.IEntryStore")]
    [InlineData("Nocturne.API.Services.Treatments.ITreatmentCache")]
    public void FindsAPortWrappedInAParameterType(string portName)
    {
        var port = Api.GetType(portName) ?? Type.GetType(portName, throwOnError: true)!;

        foreach (var parameter in new[]
                 {
                     port, typeof(Lazy<>).MakeGenericType(port), typeof(Func<>).MakeGenericType(port),
                     typeof(IEnumerable<>).MakeGenericType(port), port.MakeArrayType(),
                 })
            PortsIn(parameter).Should().Equal([port.Name], parameter.ToString());

        PortsIn(typeof(Lazy<IComparable>)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("class C { readonly Mock<IEntryStore> _store = new(); }", "C._store(IEntryStore)")]
    [InlineData("class C { object M() => Mock.Of<Nocturne.API.Services.Treatments.ITreatmentCache>(); }", "C.M(ITreatmentCache)")]
    [InlineData("using Port = Nocturne.API.Services.Entries.IEntryDecomposer; class C { }", "(file)(IEntryDecomposer)")]
    [InlineData("class C(IEntryStore store) { }", "C.C(IEntryStore)")]
    [InlineData("namespace N; class X<T> where T : IEntryStore { }", "X(IEntryStore)")]
    [InlineData("class C { void M(IServiceProvider sp) { var t = typeof(IEntryCache); sp.GetService(t); } }", "C.M(IEntryCache)")]
    [InlineData("class C { object M(IServiceProvider sp) => ActivatorUtilities.GetServiceOrCreateInstance<IEntryDecomposer>(sp); }", "C.M(IEntryDecomposer)")]
    [InlineData("class C { object M(IServiceProvider sp) => sp.GetRequiredService<IEnumerable<IEntryStore>>(); }", "C.M(IEntryStore)")]
    [InlineData("class C : ControllerBase { Task<IActionResult> Get(ITreatmentStore store) => null; }", "C.Get(ITreatmentStore)")]
    [InlineData("class H : Hub { Task Send(IDeviceStatusDecomposer d) => null; }", "H.Send(IDeviceStatusDecomposer)")]
    [InlineData("class W { Task InvokeAsync(HttpContext c, IEntryStore s) => null; }", "W.InvokeAsync(IEntryStore)")]
    [InlineData("class C { void M(WebApplication app) => app.MapGet(\"/\", Handle); static int Handle(ITreatmentCache c) => 1; }", "C.Handle(ITreatmentCache)")]
    [InlineData("class C { string N = nameof(IEntryStore); }", "C.N(IEntryStore)")]
    [InlineData("/// <see cref=\"IEntryStore\"/>\nclass C { string S = \"IEntryStore\"; } // IEntryStore", "")]
    [InlineData("class C { readonly Mock<IEntryService> _entries = new(); }", "")]
    [InlineData("class C { double M(Entry e) => EntryDecomposer.WrittenMgdl(e); }", "C.M(EntryDecomposer)")]
    [InlineData("class C { void M(JsonElement p) => JsonMergePatch.ApplyInPlace(e, p, EntryService.CarrySgvAndMgdl); }", "C.M(EntryService)")]
    [InlineData("class C { object M() => new TreatmentService(null); }", "C.M(TreatmentService)")]
    [InlineData("class C { ILogger<DeviceStatusDecomposer> L; }", "C.L(DeviceStatusDecomposer)")]
    [InlineData("using Service = Nocturne.API.Services.DeviceStatuses.DeviceStatusService; class C { }", "(file)(DeviceStatusService)")]
    [InlineData("class C { readonly Mock<ITreatmentService> _t = new(); string S = \"EntryService\"; } // TreatmentDecomposer", "")]
    public void DetectsANamedModuleType(string source, string expected) =>
        string.Join(",", NamedModuleTypes(CSharpSyntaxTree.ParseText(source)).Select(h => $"{h.Member}({h.Name})"))
            .Should().Be(expected);

    /// <summary>
    /// Constructors taking a port, as <c>Type(Port)</c>, of types declared outside the port's module.
    /// A type with no declaration in <paramref name="files"/> counts as outside.
    /// </summary>
    private static IEnumerable<string> ConstructorTakers(Assembly assembly, IReadOnlyDictionary<string, List<string>> files) =>
        assembly.GetTypes()
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .SelectMany(p => PortsIn(p.ParameterType))
                .Where(port => !(files.TryGetValue(Outermost(t).FullName!, out var paths)
                    && paths.All(ModuleOfPort[port].Owns)))
                .Select(port => $"{t.FullName!.Replace('+', '.')}({port})"))
            .Distinct();

    private static Type Outermost(Type type) => type.DeclaringType is { } outer ? Outermost(outer) : type;

    /// <summary>The ports a parameter type names, directly or as a generic argument (<c>Lazy&lt;T&gt;</c>, <c>Func&lt;T&gt;</c>).</summary>
    private static IEnumerable<string> PortsIn(Type type)
    {
        if (type.HasElementType)
            return PortsIn(type.GetElementType()!);

        if (type.IsGenericType)
            return type.GetGenericArguments().SelectMany(PortsIn);

        return ModuleOfPort.ContainsKey(type.Name) ? [type.Name] : [];
    }

    /// <summary>
    /// Every identifier in code naming a type a module declares, with the member it sits in. Doc
    /// comments and comments are trivia and a string literal is one token, so neither holds an identifier.
    /// </summary>
    private static IEnumerable<(string Member, string Name)> NamedModuleTypes(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
            .Where(name => ModuleOfName.ContainsKey(name.Identifier.ValueText))
            .Select(name => (MemberOf(name), name.Identifier.ValueText))
            .Distinct();

    /// <summary>
    /// <see cref="EnclosingMember.Of"/>, except that a primary constructor parameter is the constructor
    /// (<c>Type.Type</c>), a name in a type's base list or constraints is the type, and a name outside
    /// every type, such as a <c>using</c> alias, is <c>(file)</c>.
    /// </summary>
    private static string MemberOf(SyntaxNode node)
    {
        if (node.Ancestors().OfType<ParameterListSyntax>().FirstOrDefault()?.Parent is TypeDeclarationSyntax primary)
            return TypePath(primary) + "." + primary.Identifier.ValueText;

        return node.Ancestors().FirstOrDefault(a => a is BaseTypeDeclarationSyntax
                or (MemberDeclarationSyntax and not BaseNamespaceDeclarationSyntax)) switch
        {
            null => "(file)",
            BaseTypeDeclarationSyntax type => TypePath(type),
            _ => EnclosingMember.Of(node),
        };
    }

    private static string TypePath(BaseTypeDeclarationSyntax type) =>
        string.Join(".", type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));

    /// <summary>Each top-level type's reflection name (<c>Namespace.Name`arity</c>) to the files declaring it.</summary>
    private static Dictionary<string, List<string>> DeclaringFiles()
    {
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var source in ReflectedSources.SelectMany(RepoSources.Under))
        {
            foreach (var type in TopLevelTypes(source.Tree).OfType<BaseTypeDeclarationSyntax>())
            {
                var ns = string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
                var arity = type is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 and var count } ? $"`{count}" : "";
                var name = (ns.Length > 0 ? ns + "." : "") + type.Identifier.ValueText + arity;

                if (!files.TryGetValue(name, out var paths))
                    files[name] = paths = [];
                paths.Add(source.Path);
            }
        }

        return files;
    }

    private static IEnumerable<string> ModuleExtensionMethods(IEnumerable<SourceFile> sources) =>
        sources.Where(source => Modules.Any(m => m.Declares(source.Path)))
            .SelectMany(source => ExtensionMethods(source.Tree).Select(method => $"{source.Path}: {method}"));

    private static IEnumerable<string> OutsideDeclarations(IEnumerable<SourceFile> sources) =>
        sources.SelectMany(source => TopLevelTypeNames(source.Tree)
                .Where(name => ModuleOfName.TryGetValue(name, out var module) && !module.Owns(source.Path))
                .Select(name => $"{source.Path}: {name}"))
            .Distinct();

    /// <summary>The types declared outside any other type, delegates included.</summary>
    private static IEnumerable<MemberDeclarationSyntax> TopLevelTypes(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes(node => node is not BaseTypeDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

    private static IEnumerable<string> TopLevelTypeNames(SyntaxTree tree) =>
        TopLevelTypes(tree).Select(node => node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            _ => throw new InvalidOperationException(node.Kind().ToString()),
        });

    /// <summary>
    /// The extension methods a source declares, as <c>Type.Method</c>, in either form: a <c>this</c>
    /// parameter, or a member of an <c>extension</c> block.
    /// </summary>
    private static IEnumerable<string> ExtensionMethods(SyntaxTree tree)
    {
        var root = tree.GetRoot();

        var classic = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.ThisKeyword)));

        var blocks = root.DescendantNodes().Where(node => node.IsKind(SyntaxKind.ExtensionBlockDeclaration))
            .SelectMany(block => block.ChildNodes().OfType<MemberDeclarationSyntax>());

        return classic.Concat(blocks.OfType<MethodDeclarationSyntax>())
            .Select(method => $"{TypePath(method.Ancestors().OfType<BaseTypeDeclarationSyntax>().Last())}.{method.Identifier.ValueText}")
            .Distinct();
    }

    private static IEnumerable<SourceFile> Sources(string tree) =>
        RepoSources.Under(tree).Where(s => ModuleOfName.Keys.Any(name => s.Text.Contains(name, StringComparison.Ordinal)));
}
