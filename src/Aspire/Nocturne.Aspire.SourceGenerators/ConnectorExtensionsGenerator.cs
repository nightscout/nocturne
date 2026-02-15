using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nocturne.Aspire.SourceGenerators
{
    /// <summary>
    /// Source generator that creates Aspire connector extension methods from configuration classes
    /// marked with [ConnectorRegistration] attribute.
    /// </summary>
    [Generator]
    public class ConnectorExtensionsGenerator : IIncrementalGenerator
    {
        private const string ConnectorRegistrationAttributeFullName =
            "Nocturne.Connectors.Core.Extensions.ConnectorRegistrationAttribute";
        private const string AspireParameterAttributeFullName =
            "Nocturne.Connectors.Core.Extensions.AspireParameterAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Use CompilationProvider to access ALL referenced assemblies (including project references)
            var connectorClasses = context
                .CompilationProvider.SelectMany(
                    static (compilation, _) => GetAllConnectorTypes(compilation)
                )
                .Collect();

            // Generate extension methods for all connectors
            context.RegisterSourceOutput(
                connectorClasses,
                static (spc, connectors) => GenerateExtensions(spc, connectors)
            );
        }

        /// <summary>
        /// Scans all referenced assemblies for types with [ConnectorRegistration] attribute.
        /// This includes both project references and NuGet packages.
        /// </summary>
        private static IEnumerable<ConnectorInfo> GetAllConnectorTypes(Compilation compilation)
        {
            var connectors = new List<ConnectorInfo>();

            // Get the ConnectorRegistrationAttribute type symbol
            var attributeType = compilation.GetTypeByMetadataName(
                ConnectorRegistrationAttributeFullName
            );

            if (attributeType == null)
                return connectors;

            // Scan ALL referenced assemblies (including project references)
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // Skip system assemblies for performance
                if (assembly.Name.StartsWith("System") || assembly.Name.StartsWith("Microsoft"))
                    continue;

                foreach (var type in GetTypesInAssembly(assembly.GlobalNamespace))
                {
                    var connectorAttr = type.GetAttributes()
                        .FirstOrDefault(a =>
                            SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType)
                        );

                    if (connectorAttr != null)
                    {
                        var connectorInfo = ExtractConnectorInfo(compilation, type, connectorAttr);
                        if (connectorInfo != null)
                            connectors.Add(connectorInfo);
                    }
                }
            }

            return connectors;
        }

        /// <summary>
        /// Recursively walks the namespace tree to find all types in an assembly.
        /// </summary>
        private static IEnumerable<INamedTypeSymbol> GetTypesInAssembly(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
                yield return type;

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                foreach (var type in GetTypesInAssembly(childNs))
                    yield return type;
            }
        }

        /// <summary>
        /// Extracts connector metadata from a type symbol with [ConnectorRegistration] attribute.
        /// </summary>
        private static ConnectorInfo? ExtractConnectorInfo(
            Compilation compilation,
            INamedTypeSymbol typeSymbol,
            AttributeData connectorAttr
        )
        {
            // Extract connector registration metadata
            var connectorName = connectorAttr.ConstructorArguments[0].Value?.ToString();
            var projectTypeName = connectorAttr.ConstructorArguments[1].Value?.ToString();
            var serviceName = connectorAttr.ConstructorArguments[2].Value?.ToString();
            var environmentPrefix = connectorAttr.ConstructorArguments[3].Value?.ToString();
            var connectSourceName = connectorAttr.ConstructorArguments[4].Value?.ToString();

            // Extract optional arguments (Type and ScriptPath) if present
            // The constructor has default values, so we need to handle potential missing arguments in strictly positional context?
            // Roslyn provides arguments in order.
            // Constructor: (..., displayName, type, scriptPath)

            // Check if arguments are provided by name or position
            string type = "CSharpProject";
            string? scriptPath = null;

            // Named arguments override positional ones
            foreach (var namedArg in connectorAttr.NamedArguments)
            {
                if (namedArg.Key == "Type" && namedArg.Value.Value != null)
                {
                    if (namedArg.Value.Value is int typeVal)
                        type = typeVal == 1 ? "PythonApp" : "CSharpProject";
                    else
                        type = namedArg.Value.Value.ToString();
                }
                else if (namedArg.Key == "ScriptPath" && namedArg.Value.Value != null)
                {
                    scriptPath = namedArg.Value.Value.ToString();
                }
            }

            // Also check positional arguments if they exist beyond the required ones
            // Constructor signature: (..., type, scriptPath) at indices 10 and 11
            if (connectorAttr.ConstructorArguments.Length > 10 && connectorAttr.ConstructorArguments[10].Value != null)
            {
                 if (connectorAttr.ConstructorArguments[10].Value is int typeVal)
                    type = typeVal == 1 ? "PythonApp" : "CSharpProject";
                 else
                    type = connectorAttr.ConstructorArguments[10].Value?.ToString() ?? "CSharpProject";
            }
            if (connectorAttr.ConstructorArguments.Length > 11 && connectorAttr.ConstructorArguments[11].Value != null)
            {
                scriptPath = connectorAttr.ConstructorArguments[11].Value?.ToString();
            }

            if (
                connectorName == null
                || projectTypeName == null
                || serviceName == null
                || environmentPrefix == null
                || connectSourceName == null
            )
                return null;

            // Get the AspireParameterAttribute type symbol
            var paramAttributeType = compilation.GetTypeByMetadataName(
                AspireParameterAttributeFullName
            );
            if (paramAttributeType == null)
                return null;

            var envVarAttributeType = compilation.GetTypeByMetadataName(
                "Nocturne.Connectors.Core.Extensions.EnvironmentVariableAttribute"
            );

            // Find all properties with AspireParameterAttribute
            var parameters = new List<ParameterInfo>();
            foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                var paramAttr = member
                    .GetAttributes()
                    .FirstOrDefault(a =>
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, paramAttributeType)
                    );

                if (paramAttr == null)
                    continue;

                var paramName = paramAttr.ConstructorArguments[0].Value?.ToString();
                var configPath = paramAttr.ConstructorArguments[1].Value?.ToString();
                var isSecret =
                    paramAttr.ConstructorArguments.Length > 2
                        ? (bool?)paramAttr.ConstructorArguments[2].Value ?? false
                        : false;
                var description =
                    paramAttr.ConstructorArguments.Length > 3
                        ? paramAttr.ConstructorArguments[3].Value?.ToString()
                        : null;
                var defaultValue =
                    paramAttr.ConstructorArguments.Length > 4
                        ? paramAttr.ConstructorArguments[4].Value?.ToString()
                        : null;

                // Check for EnvironmentVariableAttribute
                string? envVarName = null;
                if (envVarAttributeType != null)
                {
                    var envVarAttr = member
                        .GetAttributes()
                        .FirstOrDefault(a =>
                            SymbolEqualityComparer.Default.Equals(
                                a.AttributeClass,
                                envVarAttributeType
                            )
                        );
                    if (envVarAttr != null)
                    {
                        envVarName = envVarAttr.ConstructorArguments[0].Value?.ToString();
                    }
                }

                if (paramName != null && configPath != null)
                {
                    parameters.Add(
                        new ParameterInfo(
                            PropertyName: member.Name,
                            ParameterName: paramName,
                            ConfigPath: configPath,
                            IsSecret: isSecret,
                            Description: description,
                            DefaultValue: defaultValue,
                            EnvironmentVariableName: envVarName
                        )
                    );
                }
            }

            return new ConnectorInfo(
                ConnectorName: connectorName,
                ProjectTypeName: projectTypeName,
                ServiceName: serviceName,
                EnvironmentPrefix: environmentPrefix,
                ConnectSourceName: connectSourceName,
                Parameters: parameters.ToImmutableArray(),
                Type: type,
                ScriptPath: scriptPath
            );
        }

        private static void GenerateExtensions(
            SourceProductionContext context,
            ImmutableArray<ConnectorInfo> connectors
        )
        {
            if (connectors.IsEmpty)
                return;

            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable ASPIREPIPELINES003"); // Experimental container image APIs
            sb.AppendLine("using System.IO;"); // Added for Path.Combine
            sb.AppendLine("using Aspire.Hosting;");
            sb.AppendLine("using Aspire.Hosting.ApplicationModel;");
            sb.AppendLine("using Aspire.Hosting.Publishing;");
            sb.AppendLine("using Microsoft.Extensions.Configuration;");
            sb.AppendLine("using Nocturne.Core.Constants;");
            sb.AppendLine("using Nocturne.Connectors.Core.Models;");
            sb.AppendLine();
            sb.AppendLine("namespace Nocturne.Aspire.Host.Extensions");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Auto-generated connector extension methods");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static partial class ConnectorExtensions");
            sb.AppendLine("    {");

            foreach (var connector in connectors)
            {
                GenerateConnectorMethod(sb, connector);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource(
                "ConnectorExtensions.g.cs",
                SourceText.From(sb.ToString(), Encoding.UTF8)
            );
        }

        private static void GenerateConnectorMethod(StringBuilder sb, ConnectorInfo connector)
        {
            var methodName = $"Add{connector.ConnectorName}Connector";
            var connectorNameLower = connector.ConnectorName.ToLower();

            sb.AppendLine();
            sb.AppendLine($"        public static IDistributedApplicationBuilder {methodName}(");
            sb.AppendLine("            this IDistributedApplicationBuilder builder,");
            sb.AppendLine("            IResourceBuilder<ProjectResource> api,");
            sb.AppendLine("            IResourceBuilder<ParameterResource> apiSecret)");
            sb.AppendLine("        {");
            sb.AppendLine(
                "            var connectors = builder.Configuration.GetSection(\"Parameters:Connectors\");"
            );
            sb.AppendLine(
                $"            var enabled = connectors.GetValue<bool>(\"{connector.ConnectorName}:Enabled\", false);"
            );
            sb.AppendLine();
            sb.AppendLine("            // In development, skip disabled connectors entirely for faster startup");
            sb.AppendLine("            // In publish mode, include all connectors - they self-disable at runtime based on env var");
            sb.AppendLine("            if (!enabled && builder.ExecutionContext.IsRunMode) return builder;");
            sb.AppendLine();

            // Generate parameter declarations
            foreach (var param in connector.Parameters)
            {
                var varName = ToCamelCase(param.PropertyName);
                var configKey =
                    $"Parameters:Connectors:{connector.ConnectorName}:{param.ConfigPath}";
                var defaultValLiteral =
                    param.DefaultValue != null ? $"\"{param.DefaultValue}\"" : "null";

                sb.AppendLine(
                    $"            var config_{varName} = builder.Configuration[\"{configKey}\"];"
                );
                sb.AppendLine(
                    $"            var val_{varName} = !string.IsNullOrEmpty(config_{varName}) ? config_{varName} : {defaultValLiteral};"
                );

                sb.AppendLine($"            var {varName} = val_{varName} is not null");
                sb.AppendLine(
                    $"                ? builder.AddParameter(\"{param.ParameterName}\", val_{varName}, secret: {param.IsSecret.ToString().ToLower()})"
                );
                sb.AppendLine(
                    $"                : builder.AddParameter(\"{param.ParameterName}\", secret: {param.IsSecret.ToString().ToLower()});"
                );

                if (!string.IsNullOrEmpty(param.Description))
                {
                    sb.AppendLine(
                        $"            {varName}.WithDescription(\"{EscapeString(param.Description)}\");"
                    );
                }
                sb.AppendLine();
            }

            // Generate common base configuration parameters (TimezoneOffset)
            GenerateBaseConfigParameters(sb, connector.ConnectorName, connectorNameLower);

            // Generate connector resource
            if (connector.Type == "PythonApp" && !string.IsNullOrEmpty(connector.ScriptPath))
            {
                // Python Connector Generation
                // Resolve path relative to AppHost directory
                // Assumes ScriptPath is something like "../../Connectors/Nocturne.Connectors.TConnectSync"
                sb.AppendLine($"            var scriptPath = Path.Combine(builder.AppHostDirectory, \"{connector.ScriptPath!.Replace("\\", "\\\\")}\");");
                sb.AppendLine("            var connector = builder");
                sb.AppendLine(
                    $"                .AddUvicornApp(\"{EscapeString(connector.ServiceName)}\", scriptPath, \"main:app\")"
                );
                sb.AppendLine("                .WithHttpHealthCheck(\"/health\")");
            }
            else
            {
                // Default C# Project Generation
                sb.AppendLine("            var connector = builder");
                sb.AppendLine(
                    $"                .AddProject<Projects.{connector.ProjectTypeName}>(\"{EscapeString(connector.ServiceName)}\")"
                );
                sb.AppendLine("                .WithHttpEndpoint(port: 0, name: \"http\")");
            }

            // Common configuration
            sb.AppendLine(
                "                .WithEnvironment(\"NocturneApiUrl\", api.GetEndpoint(\"api\"))"
            );
            sb.AppendLine("                .WithEnvironment(\"ApiSecret\", apiSecret)");
            sb.AppendLine("                .WaitFor(api)");
            sb.AppendLine("                .WithReference(api);");

            // For python apps, we specifically need to bind the environment variables to the resource
            // The existing code chained .WithEnvironment calls on 'connector' variable, which is good.

            // Inject configuration as environment variables
            foreach (var param in connector.Parameters)
            {
                if (!string.IsNullOrEmpty(param.EnvironmentVariableName))
                {
                    var varName = ToCamelCase(param.PropertyName);
                    sb.AppendLine(
                        $"            connector.WithEnvironment(\"{param.EnvironmentVariableName}\", {varName});"
                    );
                }
            }

            // Inject base configuration environment variables
            sb.AppendLine(
                $"            connector.WithEnvironment(\"TimezoneOffset\", timezoneOffset);"
            );
            // Pass the enabled flag so connectors can self-disable at runtime
            sb.AppendLine(
                $"            connector.WithEnvironment(\"Parameters__Connectors__{connector.ConnectorName}__Enabled\", enabled.ToString().ToLower());"
            );
            sb.AppendLine();

            // Set parent relationships
            foreach (var param in connector.Parameters)
            {
                var varName = ToCamelCase(param.PropertyName);
                sb.AppendLine($"            {varName}.WithParentRelationship(connector);");
            }
            sb.AppendLine($"            timezoneOffset.WithParentRelationship(connector);");

            // Add reference from API to connector for service discovery
            // This allows the API to resolve the connector's endpoint for health checks
            sb.AppendLine();
            sb.AppendLine("            api.WithReference(connector);");

            // Publish as Docker Compose service (called after all other configuration)
            sb.AppendLine("            connector.PublishAsDockerComposeService((_,_) => { });");

            // Configure multi-arch container build for amd64 and arm64 (supports Mac Apple Silicon)
            sb.AppendLine("            connector.WithContainerBuildOptions(options =>");
            sb.AppendLine("            {");
            sb.AppendLine("                options.TargetPlatform = ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;");
            sb.AppendLine("            });");

            // Configure remote image from GitHub Container Registry
            sb.AppendLine($"            connector.WithRemoteImageName(\"ghcr.io/nightscout/nocturne/{connectorNameLower}\");");
            sb.AppendLine($"            connector.WithRemoteImageTag(\"latest\");");

            sb.AppendLine();
            sb.AppendLine("            return builder;");
            sb.AppendLine("        }");
        }

        /// <summary>
        /// Generates common base configuration parameters that all connectors support.
        /// These are defined in BaseConnectorConfiguration and should be available for all connectors.
        /// </summary>
        private static void GenerateBaseConfigParameters(
            StringBuilder sb,
            string connectorName,
            string connectorNameLower
        )
        {
            // TimezoneOffset parameter
            sb.AppendLine(
                $"            var config_timezoneOffset = builder.Configuration[\"Parameters:Connectors:{connectorName}:TimezoneOffset\"];"
            );
            sb.AppendLine(
                $"            var val_timezoneOffset = !string.IsNullOrEmpty(config_timezoneOffset) ? config_timezoneOffset : \"0\";"
            );
            sb.AppendLine($"            var timezoneOffset = val_timezoneOffset is not null");
            sb.AppendLine(
                $"                ? builder.AddParameter(\"{connectorNameLower}-timezone-offset\", val_timezoneOffset, secret: false)"
            );
            sb.AppendLine(
                $"                : builder.AddParameter(\"{connectorNameLower}-timezone-offset\", secret: false);"
            );
            sb.AppendLine(
                $"            timezoneOffset.WithDescription(\"Timezone offset in hours to convert pump local time to UTC\");"
            );
            sb.AppendLine();
        }

        private static string ToCamelCase(string str)
        {
            if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
                return str;
            return char.ToLower(str[0]) + str.Substring(1);
        }

        private static string EscapeString(string? str)
        {
            if (str == null)
                return string.Empty;
            return str.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private record ConnectorInfo(
            string ConnectorName,
            string ProjectTypeName,
            string ServiceName,
            string EnvironmentPrefix,
            string ConnectSourceName,
            ImmutableArray<ParameterInfo> Parameters,
            string Type = "CSharpProject",
            string? ScriptPath = null
        );

        private record ParameterInfo(
            string PropertyName,
            string ParameterName,
            string ConfigPath,
            bool IsSecret,
            string? Description,
            string? DefaultValue,
            string? EnvironmentVariableName
        );
    }
}
