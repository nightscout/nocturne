using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Nocturne.API.OpenApi;

/// <summary>
/// What the Scalar reference shows for a controller, read off the controller itself: its
/// sidebar entry is the controller, its group is the area named by <c>[Tags]</c>, and its page
/// is the class's XML <c>&lt;summary&gt;</c>. A <see cref="DisplayNameAttribute"/> on the class
/// overrides the label derived from the class name.
/// </summary>
public static partial class ControllerDocs
{
    public sealed record Entry(string Tag, string Label, string? Area, string? Summary);

    private static readonly Lazy<Dictionary<string, XElement>> Summaries = new(LoadSummaries);

    public static Entry? For(ApiDescription description) =>
        description.ActionDescriptor is ControllerActionDescriptor action
            ? For(action.ControllerTypeInfo)
            : null;

    public static Entry For(Type controller)
    {
        var label = controller.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
            ?? ControllerNameTagOperationProcessor.TagFor(controller);
        var area = controller.GetCustomAttribute<TagsAttribute>()?.Tags.FirstOrDefault();

        // V1, V2 and V3 repeat controller names (Entries, Treatments, Profile, ...) in one document.
        var tag = IsNightscout(controller) && area is not null
            ? $"{area} {ControllerNameTagOperationProcessor.TagFor(controller)}"
            : ControllerNameTagOperationProcessor.TagFor(controller);

        return new Entry(tag, label, area, SummaryOf(controller));
    }

    /// <summary>The anchor Scalar gives a tag: <c>#document/tag/{slug}</c>.</summary>
    public static string Slug(string tag) => tag.ToLowerInvariant().Replace(' ', '-');

    private static bool IsNightscout(Type controller) =>
        !ApiDocumentMembership.InNocturneDocument(controller.Namespace ?? string.Empty);

    private static string? SummaryOf(Type controller) =>
        Summaries.Value.TryGetValue("T:" + controller.FullName, out var summary)
            ? Render(summary)
            : null;

    private static Dictionary<string, XElement> LoadSummaries()
    {
        var path = Path.ChangeExtension(typeof(ControllerDocs).Assembly.Location, ".xml");
        if (!File.Exists(path))
            return [];

        return XDocument.Load(path, LoadOptions.PreserveWhitespace)
            .Descendants("member")
            .Where(member => member.Attribute("name")?.Value.StartsWith("T:", StringComparison.Ordinal) == true
                && member.Element("summary") is not null)
            .ToDictionary(member => member.Attribute("name")!.Value, member => member.Element("summary")!);
    }

    private static string Render(XElement summary)
    {
        var text = new StringBuilder();
        foreach (var node in summary.Nodes())
        {
            switch (node)
            {
                case XText plain:
                    text.Append(plain.Value);
                    break;
                case XElement { Name.LocalName: "c" } code:
                    text.Append('`').Append(code.Value).Append('`');
                    break;
                case XElement { Name.LocalName: "para" } paragraph:
                    text.Append("\n\n").Append(Render(paragraph)).Append("\n\n");
                    break;
                case XElement reference when reference.Attribute("cref") is { } cref:
                    text.Append('`').Append(ShortName(cref.Value)).Append('`');
                    break;
                case XElement other:
                    text.Append(other.Attribute("langword")?.Value ?? other.Attribute("name")?.Value ?? other.Value);
                    break;
            }
        }

        var paragraphs = text.ToString()
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => Whitespace().Replace(paragraph, " ").Trim())
            .Where(paragraph => paragraph.Length > 0);

        return string.Join("\n\n", paragraphs);
    }

    private static string ShortName(string cref)
    {
        var name = cref[(cref.IndexOf(':') + 1)..];
        var arguments = name.IndexOf('(');
        if (arguments >= 0)
            name = name[..arguments];
        name = name[(name.LastIndexOf('.') + 1)..];
        var arity = name.IndexOf('`');
        return arity >= 0 ? name[..arity] : name;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
