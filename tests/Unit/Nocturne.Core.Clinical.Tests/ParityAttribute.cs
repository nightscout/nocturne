namespace Nocturne.Core.Clinical.Tests;

/// <summary>
/// Marks tests that verify 1:1 parity with legacy Nightscout functionality. A local copy of
/// <c>Nocturne.Tests.Shared.Attributes.ParityAttribute</c>, whose project brings in
/// Infrastructure.Data and the ASP.NET Core framework, which these tests do not need.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ParityAttribute : Attribute { }
