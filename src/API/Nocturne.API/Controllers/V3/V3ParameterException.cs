namespace Nocturne.API.Controllers.V3;

/// <summary>A V3 request parameter Nightscout answers with 400 and a message.</summary>
/// <seealso cref="Filters.V3ParameterExceptionFilterAttribute"/>
public abstract class V3ParameterException(string message) : Exception(message);

/// <summary>Thrown when a parameter is out of tolerance, such as a negative limit.</summary>
public sealed class V3ParameterOutOfToleranceException(string parameterName)
    : V3ParameterException($"Parameter {parameterName} out of tolerance")
{
    public string ParameterName { get; } = parameterName;
}

/// <summary>Thrown when a request names both <c>sort</c> and <c>sort$desc</c>.</summary>
public sealed class V3SortConflictException()
    : V3ParameterException("Parameters sort and sort_desc cannot be combined");
