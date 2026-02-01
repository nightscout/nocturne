using System.Collections.Concurrent;
using System.Reflection;
using System.Xml;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Services;

/// <summary>
/// Service for extracting XML documentation comments from methods
/// </summary>
public interface IXmlDocumentationService
{
    /// <summary>
    /// Get the summary documentation for a method
    /// </summary>
    /// <param name="methodInfo">The method to get documentation for</param>
    /// <returns>The summary text or null if not found</returns>
    string? GetMethodSummary(MethodInfo methodInfo);
}

/// <summary>
/// Implementation of XML documentation service with lazy loading.
/// XML documentation is only loaded when first requested for a specific assembly,
/// reducing startup memory overhead.
/// </summary>
public class XmlDocumentationService : IXmlDocumentationService
{
    // Use ConcurrentDictionary for thread-safe lazy loading
    private readonly ConcurrentDictionary<string, Lazy<XmlDocument?>> _xmlDocuments = new();
    private readonly ILogger<XmlDocumentationService> _logger;

    public XmlDocumentationService(ILogger<XmlDocumentationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get the summary documentation for a method
    /// </summary>
    /// <param name="methodInfo">The method to get documentation for</param>
    /// <returns>The summary text or null if not found</returns>
    public string? GetMethodSummary(MethodInfo methodInfo)
    {
        if (methodInfo?.DeclaringType == null)
            return null;

        try
        {
            var assembly = methodInfo.DeclaringType.Assembly;
            var assemblyName = assembly.GetName().Name;
            if (assemblyName == null)
                return null;

            var xmlDoc = GetOrLoadXmlDocument(assembly, assemblyName);
            if (xmlDoc == null)
                return null;

            var memberName = GetMemberName(methodInfo);
            var memberNode = xmlDoc.SelectSingleNode($"//member[@name='{memberName}']");
            var summaryNode = memberNode?.SelectSingleNode("summary");

            if (summaryNode?.InnerText != null)
            {
                // Clean up the XML text (remove extra whitespace, newlines)
                return summaryNode
                    .InnerText.Trim()
                    .Replace("\n", " ")
                    .Replace("\r", "")
                    .Replace("  ", " ")
                    .Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not extract XML documentation for method {Method}",
                methodInfo.Name
            );
        }

        return null;
    }

    /// <summary>
    /// Get or lazily load the XML documentation for a specific assembly
    /// </summary>
    private XmlDocument? GetOrLoadXmlDocument(Assembly assembly, string assemblyName)
    {
        var lazyDoc = _xmlDocuments.GetOrAdd(
            assemblyName,
            _ => new Lazy<XmlDocument?>(
                () => LoadXmlDocumentForAssembly(assembly, assemblyName),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        return lazyDoc.Value;
    }

    /// <summary>
    /// Load XML documentation file for a specific assembly
    /// </summary>
    private XmlDocument? LoadXmlDocumentForAssembly(Assembly assembly, string assemblyName)
    {
        try
        {
            var assemblyLocation = assembly.Location;
            if (string.IsNullOrEmpty(assemblyLocation))
                return null;

            var xmlPath = Path.ChangeExtension(assemblyLocation, ".xml");

            if (!File.Exists(xmlPath))
                return null;

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(xmlPath);

            _logger.LogDebug("Loaded XML documentation for assembly: {AssemblyName}", assemblyName);

            return xmlDoc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not load XML documentation for assembly {Assembly}",
                assemblyName
            );
            return null;
        }
    }

    /// <summary>
    /// Generate the XML member name for a method
    /// </summary>
    /// <param name="methodInfo">The method info</param>
    /// <returns>The XML member name</returns>
    private static string GetMemberName(MethodInfo methodInfo)
    {
        var declaringType = methodInfo.DeclaringType;
        if (declaringType == null)
            return string.Empty;

        var methodName = methodInfo.Name;
        var typeName = declaringType.FullName?.Replace('+', '.');

        // Handle method parameters
        var parameters = methodInfo.GetParameters();
        if (parameters.Length > 0)
        {
            var parameterTypes = parameters.Select(p => p.ParameterType.FullName).ToArray();
            methodName += $"({string.Join(",", parameterTypes)})";
        }

        return $"M:{typeName}.{methodName}";
    }
}
