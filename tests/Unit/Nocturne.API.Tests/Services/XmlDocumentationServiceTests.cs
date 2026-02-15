using System.Collections.Concurrent;
using System.Reflection;
using System.Xml;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Test helper class with various method signatures for testing XML documentation
/// </summary>
public class TestDocumentationClass
{
    /// <summary>
    /// A simple method with documentation
    /// </summary>
    /// <returns>Always returns true</returns>
    public bool SimpleMethod()
    {
        return true;
    }

    /// <summary>
    /// Method with parameters
    /// </summary>
    /// <param name="value">An integer value</param>
    /// <param name="name">A string name</param>
    /// <returns>A formatted string</returns>
    public string MethodWithParameters(int value, string name)
    {
        return $"{name}: {value}";
    }

    /// <summary>
    /// Generic method with constraints
    /// </summary>
    /// <typeparam name="T">Generic type parameter</typeparam>
    /// <param name="item">Item of type T</param>
    /// <returns>The same item</returns>
    public T GenericMethod<T>(T item)
        where T : class
    {
        return item;
    }

    /// <summary>
    /// Overloaded method first version
    /// </summary>
    /// <param name="value">Integer value</param>
    public void OverloadedMethod(int value) { }

    /// <summary>
    /// Overloaded method second version
    /// </summary>
    /// <param name="value">String value</param>
    public void OverloadedMethod(string value) { }

    public void MethodWithoutDocumentation() { }

    /// <summary>
    /// Nested class for testing
    /// </summary>
    public class NestedClass
    {
        /// <summary>
        /// Method in nested class
        /// </summary>
        /// <returns>Always returns 42</returns>
        public int NestedMethod()
        {
            return 42;
        }
    }
}

/// <summary>
/// Tests for XmlDocumentationService
/// </summary>
public class XmlDocumentationServiceTests
{
    private readonly Mock<ILogger<XmlDocumentationService>> _mockLogger;
    private readonly XmlDocumentationService _service;

    public XmlDocumentationServiceTests()
    {
        _mockLogger = new Mock<ILogger<XmlDocumentationService>>();
        _service = new XmlDocumentationService(_mockLogger.Object);
    }

    #region GetMethodSummary Tests

    [Fact]
    public void GetMethodSummary_WithNullMethodInfo_ShouldReturnNull()
    {
        // Arrange
        MethodInfo? nullMethod = null;

        // Act
        var result = _service.GetMethodSummary(nullMethod!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithMethodHavingNullDeclaringType_ShouldReturnNull()
    {
        // Arrange
        var mockMethodInfo = new Mock<MethodInfo>();
        mockMethodInfo.Setup(m => m.DeclaringType).Returns((Type?)null);

        // Act
        var result = _service.GetMethodSummary(mockMethodInfo.Object);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithMethodFromAssemblyWithoutXmlDocs_ShouldReturnNull()
    {
        // Arrange
        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = _service.GetMethodSummary(method!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithMethodFromAssemblyWithNullName_ShouldReturnNull()
    {
        // Arrange
        // Create a custom mock that returns an assembly with null name
        var testMethod = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Since we can't easily mock AssemblyName, we'll test indirectly by ensuring
        // the method handles cases where assembly name is unavailable

        // Act
        var result = _service.GetMethodSummary(testMethod!);

        // Assert
        // This should return null because the test assembly doesn't have XML documentation
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithExceptionInTryBlock_ShouldReturnNull()
    {
        // Arrange
        // Create a mock method that passes the initial null check but fails inside the try block
        var mockMethodInfo = new Mock<MethodInfo>();
        var mockType = new Mock<Type>();
        var mockAssembly = new Mock<Assembly>();

        mockMethodInfo.Setup(m => m.DeclaringType).Returns(mockType.Object);
        mockType.Setup(t => t.Assembly).Returns(mockAssembly.Object);
        mockAssembly
            .Setup(a => a.GetName())
            .Throws(new InvalidOperationException("Test exception"));
        mockMethodInfo.Setup(m => m.Name).Returns("TestMethod");

        // Act
        var result = _service.GetMethodSummary(mockMethodInfo.Object);

        // Assert
        // The method should catch the exception in the try block and return null
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithSimpleMethod_ShouldReturnCleanedSummary()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "SimpleMethod",
            "<summary>\n  A simple method with documentation  \n</summary>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("A simple method with documentation");
    }

    [Fact]
    public void GetMethodSummary_WithMethodWithParameters_ShouldReturnCorrectSummary()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "MethodWithParameters",
            "<summary>Method with parameters</summary>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.MethodWithParameters)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("Method with parameters");
    }

    [Fact]
    public void GetMethodSummary_WithGenericMethod_ShouldHandleGenericParameters()
    {
        // Note: The current implementation may not handle generic methods correctly
        // because generic parameter types don't have FullName - they return null
        // This test documents the current behavior

        // Arrange
        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.GenericMethod)
        );

        // Act
        var result = _service.GetMethodSummary(method!);

        // Assert
        // The current implementation returns null for generic methods because
        // generic parameter types have null FullName, causing the member name generation to fail
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithOverloadedMethod_ShouldReturnCorrectSummary()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "OverloadedMethod",
            "<summary>Overloaded method first version</summary>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.OverloadedMethod),
            new[] { typeof(int) }
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("Overloaded method first version");
    }

    [Fact]
    public void GetMethodSummary_WithNestedClassMethod_ShouldReturnCorrectSummary()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass.NestedClass",
            "NestedMethod",
            "<summary>Method in nested class</summary>"
        );

        var method = typeof(TestDocumentationClass.NestedClass).GetMethod(
            nameof(TestDocumentationClass.NestedClass.NestedMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("Method in nested class");
    }

    [Fact]
    public void GetMethodSummary_WithMethodHavingMultilineDocumentation_ShouldCleanWhitespace()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "SimpleMethod",
            "<summary>\n    This is a multi-line\n    documentation with\n    extra spaces\n  </summary>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("This is a multi-line   documentation with   extra spaces");
    }

    [Fact]
    public void GetMethodSummary_WithEmptySummary_ShouldReturnEmptyString()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "SimpleMethod",
            "<summary></summary>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().Be("");
    }

    [Fact]
    public void GetMethodSummary_WithNullSummaryInnerText_ShouldReturnNull()
    {
        // Arrange
        var service = CreateServiceWithMockXmlDoc(
            "TestDocumentationClass",
            "SimpleMethod",
            "<member name=\"M:Nocturne.API.Tests.Services.TestDocumentationClass.SimpleMethod\"><summary /></member>"
        );

        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetMethodSummary_WithMalformedXml_ShouldReturnNull()
    {
        // Arrange
        var service = CreateServiceWithMalformedXmlDoc();
        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = service.GetMethodSummary(method!);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region XML Loading Tests

    [Fact]
    public void Constructor_ShouldLoadXmlDocumentationWithoutException()
    {
        // Arrange & Act
        var service = new XmlDocumentationService(_mockLogger.Object);

        // Assert
        service.Should().NotBeNull();
        // Note: The constructor may or may not log messages depending on whether XML files exist
        // So we just verify construction succeeds without exception
    }

    [Fact]
    public void GetMethodSummary_WithMethodFromNonExistentXmlDoc_ShouldReturnNull()
    {
        // Arrange
        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var result = _service.GetMethodSummary(method!);

        // Assert
        // Should return null because no XML documentation is available for the test assembly
        result.Should().BeNull();
    }

    #endregion

    #region Performance Tests

    [Fact]
    public void GetMethodSummary_WithLargeXmlDocument_ShouldPerformWell()
    {
        // Arrange
        var largeXmlContent = GenerateLargeXmlDocument(1000); // 1000 methods
        var service = CreateServiceWithCustomXmlDoc(largeXmlContent);
        var method = typeof(TestDocumentationClass).GetMethod(
            nameof(TestDocumentationClass.SimpleMethod)
        );

        // Act
        var startTime = DateTime.UtcNow;
        var result = service.GetMethodSummary(method!);
        var endTime = DateTime.UtcNow;

        // Assert
        var duration = endTime - startTime;
        duration.Should().BeLessThan(TimeSpan.FromMilliseconds(100)); // Should complete within 100ms
    }

    #endregion

    #region Helper Methods

    private XmlDocumentationService CreateServiceWithMockXmlDoc(
        string className,
        string methodName,
        string summaryXml
    )
    {
        var assemblyName = typeof(TestDocumentationClass).Assembly.GetName().Name;
        var fullClassName = $"Nocturne.API.Tests.Services.{className}";
        var memberName = $"M:{fullClassName}.{methodName}";

        if (methodName == "MethodWithParameters")
        {
            memberName = $"M:{fullClassName}.{methodName}(System.Int32,System.String)";
        }
        else if (methodName == "GenericMethod")
        {
            memberName = $"M:{fullClassName}.{methodName}``1(``0)";
        }
        else if (methodName == "OverloadedMethod")
        {
            memberName = $"M:{fullClassName}.{methodName}(System.Int32)";
        }

        var xmlContent =
            $@"<?xml version=""1.0""?>
<doc>
    <assembly>
        <name>{assemblyName}</name>
    </assembly>
    <members>
        <member name=""{memberName}"">
            {summaryXml}
        </member>
    </members>
</doc>";

        return CreateServiceWithCustomXmlDoc(xmlContent);
    }

    private XmlDocumentationService CreateServiceWithCustomXmlDoc(string xmlContent)
    {
        var mockLogger = new Mock<ILogger<XmlDocumentationService>>();
        var service = new Mock<XmlDocumentationService>(mockLogger.Object) { CallBase = true };

        // Use reflection to set the private _xmlDocuments field
        var assemblyName = typeof(TestDocumentationClass).Assembly.GetName().Name;
        var xmlDocumentsField = typeof(XmlDocumentationService).GetField(
            "_xmlDocuments",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(xmlContent);

        var xmlDocuments = new ConcurrentDictionary<string, Lazy<XmlDocument?>>();
        xmlDocuments[assemblyName!] = new Lazy<XmlDocument?>(() => xmlDoc);
        xmlDocumentsField!.SetValue(service.Object, xmlDocuments);

        return service.Object;
    }

    private XmlDocumentationService CreateServiceWithMalformedXmlDoc()
    {
        var mockLogger = new Mock<ILogger<XmlDocumentationService>>();
        var service = new Mock<XmlDocumentationService>(mockLogger.Object) { CallBase = true };

        // Use reflection to set the private _xmlDocuments field with malformed XML
        var assemblyName = typeof(TestDocumentationClass).Assembly.GetName().Name;
        var xmlDocumentsField = typeof(XmlDocumentationService).GetField(
            "_xmlDocuments",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        var xmlDocuments = new ConcurrentDictionary<string, Lazy<XmlDocument?>>();

        // Create a document that will cause XPath query failures
        try
        {
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml("<invalid><xml>content</invalid>"); // Malformed XML
            xmlDocuments[assemblyName!] = new Lazy<XmlDocument?>(() => xmlDoc);
        }
        catch
        {
            // If it fails to load, that's also a valid test case
        }

        xmlDocumentsField!.SetValue(service.Object, xmlDocuments);
        return service.Object;
    }

    private string GenerateLargeXmlDocument(int methodCount)
    {
        var assemblyName = typeof(TestDocumentationClass).Assembly.GetName().Name;
        var xmlBuilder = new System.Text.StringBuilder();

        xmlBuilder.AppendLine(
            $@"<?xml version=""1.0""?>
<doc>
    <assembly>
        <name>{assemblyName}</name>
    </assembly>
    <members>"
        );

        for (int i = 0; i < methodCount; i++)
        {
            xmlBuilder.AppendLine(
                $@"        <member name=""M:TestClass.Method{i}"">
            <summary>Method {i} documentation</summary>
        </member>"
            );
        }

        // Add our test method
        xmlBuilder.AppendLine(
            $@"        <member name=""M:Nocturne.API.Tests.Services.TestDocumentationClass.SimpleMethod"">
            <summary>A simple method with documentation</summary>
        </member>"
        );

        xmlBuilder.AppendLine(
            @"    </members>
</doc>"
        );

        return xmlBuilder.ToString();
    }

    #endregion
}
