using YamlDotNet.RepresentationModel;
using static Nocturne.Aspire.Host.Tests.Publishing.ComposeYaml;

namespace Nocturne.Aspire.Host.Tests.Publishing;

public abstract class ComposeTransformTestBase : IDisposable
{
    protected const string Postgres = "nocturne-postgres";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();

    public void Dispose() => _dir.Delete(recursive: true);

    protected string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    protected static YamlMappingNode Configs(string compose)
        => (YamlMappingNode)Root(compose)["configs"];

    protected static YamlMappingNode ConfigReference(YamlMappingNode service)
        => (YamlMappingNode)((YamlSequenceNode)service["configs"]).Children.Single();
}
