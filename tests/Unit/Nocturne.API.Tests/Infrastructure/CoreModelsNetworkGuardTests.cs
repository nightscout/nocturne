using System.Text.RegularExpressions;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>Nocturne.Core.Models</c> holds the domain types every project shares, so it opens no socket
/// and resolves no name: a model that did would hand every consumer a network call it did not ask
/// for. Code that does I/O lives in an infrastructure project; the outbound request guard is
/// <c>Nocturne.Infrastructure.Outbound</c>.
/// </summary>
/// <remarks>
/// A source scan of the code lines of each file. It cannot see a call reached through a package
/// or a type it does not name (<c>IPAddress.Parse</c> and <c>IPAddress.IsLoopback</c> do no I/O
/// and pass), a name aliased by a <c>using</c> alias, or a reference added to the project file
/// without a use in source. <see cref="AllowedFiles"/> lists a pure file that has to name one of
/// the types by file and reason; it is empty because no file needs it.
/// </remarks>
[Trait("Category", "Unit")]
public partial class CoreModelsNetworkGuardTests
{
    private const string Folder = "src/Core/Nocturne.Core.Models";

    private static readonly IReadOnlyDictionary<string, string> AllowedFiles = new Dictionary<string, string>();

    [GeneratedRegex(@"System\.Net\.Sockets|\bDns\.|\b(?:HttpClient|HttpClientHandler|HttpMessageInvoker|HttpMessageHandler|SocketsHttpHandler|TcpClient|UdpClient)\b")]
    private static partial Regex NetworkApi();

    [Fact]
    public void NoModelFileReferencesASocketOrDnsApi()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, Folder), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/") && !path.Contains("/bin/"))
            .Where(path => !AllowedFiles.ContainsKey(path))
            .Where(path => File.ReadLines(Path.Combine(root, path))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(NetworkApi().IsMatch))
            .ToList();

        offenders.Should().BeEmpty("the models project does no network I/O; a type that does belongs in an infrastructure project");
    }

    [Theory]
    [InlineData("using System.Net.Sockets;")]
    [InlineData("var addresses = await Dns.GetHostAddressesAsync(host);")]
    [InlineData("private static readonly HttpClient Client = new();")]
    [InlineData("new SocketsHttpHandler();")]
    [InlineData("new HttpMessageInvoker(new HttpClientHandler());")]
    [InlineData("using var client = new TcpClient(host, port);")]
    public void TheGuardSeesTheApisItForbids(string line)
    {
        NetworkApi().IsMatch(line).Should().BeTrue();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}
