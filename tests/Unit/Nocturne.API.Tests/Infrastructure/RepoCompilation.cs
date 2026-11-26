using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Compilations of the repository's sources, for the guards that need a semantic model: what a name
/// or an expression is, not what it is spelled as. Source generators do not run, so a member a
/// generator would add is unbound; a guard asserts that every node it depends on is bound.
/// </summary>
internal static class RepoCompilation
{
    private const string ApiAssembly = "Nocturne.API";

    private const string TestAssembly = "Nocturne.API.Tests";

    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> ReferenceSet =
        new(ReadReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<CSharpCompilation> ApiCompilation =
        new(() => Of(
            ApiAssembly,
            RepoSources.Under("src/API").Select(file => file.Tree).Append(CSharpSyntaxTree.ParseText(ImplicitUsings))),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The <c>src/API</c> sources, compiled once per test process.</summary>
    public static CSharpCompilation Api => ApiCompilation.Value;

    /// <summary>
    /// A compilation of <paramref name="trees"/> against everything the API project compiles
    /// against. A test assembly name sees <c>Nocturne.Infrastructure.Data</c>'s internals, as the
    /// API's own does not.
    /// </summary>
    public static CSharpCompilation Of(string assemblyName, IEnumerable<SyntaxTree> trees) =>
        CSharpCompilation.Create(
            assemblyName,
            trees,
            ReferenceSet.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>
    /// A compilation of one inline <paramref name="source"/> beside a <paramref name="support"/> file
    /// that holds the usings and declarations the snippets of a guard share.
    /// </summary>
    public static CSharpCompilation OfSnippet(string source, string support) =>
        Of(TestAssembly, [CSharpSyntaxTree.ParseText(ImplicitUsings), CSharpSyntaxTree.ParseText(support), CSharpSyntaxTree.ParseText(source)]);

    private static List<MetadataReference> ReadReferences()
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in trusted.Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name is ApiAssembly or TestAssembly || references.ContainsKey(name) || !IsManaged(path))
                continue;

            references.Add(name, MetadataReference.CreateFromFile(path));
        }

        return [.. references.Values];
    }

    private static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
