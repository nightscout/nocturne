namespace Nocturne.API.Attributes;

/// <summary>
/// Specifies the property name used by the generated ApiClient for this controller's NSwag client.
/// Applied at the controller class level so codegen can derive apiClient.{propertyName}.method() calls.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ClientPropertyNameAttribute(string propertyName) : Attribute
{
    public string PropertyName { get; } = propertyName;
}
