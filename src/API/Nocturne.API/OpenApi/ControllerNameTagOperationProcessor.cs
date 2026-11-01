using System.Text.RegularExpressions;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.OpenApi;

/// <summary>
/// Forces NSwag to use the controller name (without "Controller" suffix) as the tag,
/// ignoring any [Tags] attributes. This keeps the TypeScript client codegen granular
/// (one client class per controller) while [Tags] attributes only affect the runtime
/// Microsoft OpenAPI pipeline used by Scalar.
///
/// PascalCase names are split into space-separated words so that the remote-codegen
/// <c>tagToFileName</c> function can produce correct camelCase filenames
/// (e.g. "PatientRecord" → "Patient Record" → "patientRecords.generated.remote.ts").
/// </summary>
public sealed partial class ControllerNameTagOperationProcessor : IOperationProcessor
{
    public static string TagFor(Type controller)
    {
        var controllerName = controller.Name;

        if (controllerName.EndsWith("Controller", StringComparison.Ordinal))
            controllerName = controllerName[..^"Controller".Length];

        // Split PascalCase into space-separated words for remote-codegen compatibility.
        // "PatientRecord" → "Patient Record", "BGCheck" → "BG Check"
        return PascalCaseBoundary().Replace(controllerName, " ");
    }

    public bool Process(OperationProcessorContext context)
    {
        context.OperationDescription.Operation.Tags.Clear();
        context.OperationDescription.Operation.Tags.Add(TagFor(context.ControllerType));

        return true;
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex PascalCaseBoundary();
}
