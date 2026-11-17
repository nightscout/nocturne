using YamlDotNet.RepresentationModel;

namespace Nocturne.Aspire.Host.Tests.Publishing;

internal static class ComposeYaml
{
    public const string EnableLabel = "com.centurylinklabs.watchtower.enable";

    public static YamlMappingNode Root(string compose)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(compose));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    public static YamlMappingNode Service(string compose, string name)
        => (YamlMappingNode)((YamlMappingNode)Root(compose)["services"])[name];

    public static string? Text(YamlNode node) => ((YamlScalarNode)node).Value;

    public static string? Label(string compose, string service)
        => Text(((YamlMappingNode)Service(compose, service)["labels"])[EnableLabel]);

    public static string? EnvValue(string compose, string service, string key)
        => Text(((YamlMappingNode)Service(compose, service)["environment"])[key]);
}
