using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.OpenApi;
using NSwag;
using NSwag.Generation.Processors.Contexts;

namespace Nocturne.API.Tests.OpenApi;

/// <summary>
/// The NSwag document feeds the published SDK specs, so a scheme or requirement missing here
/// leaves every generated SDK without authentication plumbing.
/// </summary>
public class NSwagSecurityProcessorTests
{
    private static OpenApiDocument DocumentWithSchemes()
    {
        var document = new OpenApiDocument();
        new SecuritySchemeDocumentProcessor().Process(
            new DocumentProcessorContext(document, [], [], null!, null!, null!));
        return document;
    }

    private static OpenApiOperation ProcessOperation(Type controllerType, string methodName)
    {
        var description = new OpenApiOperationDescription { Operation = new OpenApiOperation() };
        var context = new OperationProcessorContext(
            new OpenApiDocument(),
            description,
            controllerType,
            controllerType.GetMethod(methodName)!,
            null!,
            null!,
            null!,
            []);

        new SecurityRequirementOperationProcessor().Process(context);
        return description.Operation;
    }

    [Fact]
    public void DocumentProcessor_RegistersBearerScheme()
    {
        var bearer = DocumentWithSchemes().Components.SecuritySchemes["bearer"];

        bearer.Type.Should().Be(OpenApiSecuritySchemeType.Http);
        bearer.Scheme.Should().Be("bearer");
    }

    [Fact]
    public void DocumentProcessor_RegistersOAuth2SchemeWithAuthorizationCodeFlow()
    {
        var oauth2 = DocumentWithSchemes().Components.SecuritySchemes["oauth2"];

        oauth2.Type.Should().Be(OpenApiSecuritySchemeType.OAuth2);
        oauth2.Flows!.AuthorizationCode!.AuthorizationUrl.Should().Be("/api/oauth/authorize");
        oauth2.Flows.AuthorizationCode.TokenUrl.Should().Be("/api/oauth/token");
        oauth2.Flows.AuthorizationCode.Scopes.Should().ContainKey("*");
    }

    [Fact]
    public void DocumentProcessor_RegistersInstanceKeyHeaderScheme()
    {
        var instanceKey = DocumentWithSchemes().Components.SecuritySchemes["instanceKey"];

        instanceKey.Type.Should().Be(OpenApiSecuritySchemeType.ApiKey);
        instanceKey.Name.Should().Be("X-Instance-Key");
        instanceKey.In.Should().Be(OpenApiSecurityApiKeyLocation.Header);
    }

    [Fact]
    public void OperationProcessor_ActionCoveredByTheFallbackPolicy_DeclaresEverySchemeAsAnAlternative()
    {
        var operation = ProcessOperation(typeof(GuestLinkController), nameof(GuestLinkController.CreateGuestLink));

        operation.Security.Should().HaveCount(3);
        operation.Security.SelectMany(requirement => requirement.Keys)
            .Should().BeEquivalentTo(["oauth2", "bearer", "instanceKey"]);
        operation.Security.First()["oauth2"].Should().BeEquivalentTo(["*"]);
    }

    [Fact]
    public void OperationProcessor_AnonymousAction_DeclaresNoSecurity()
    {
        var operation = ProcessOperation(typeof(GuestLinkController), nameof(GuestLinkController.ActivateGuestLink));

        operation.Security.Should().BeNullOrEmpty();
    }

    [Fact]
    public void OperationProcessor_AnonymousController_DeclaresNoSecurity()
    {
        var operation = ProcessOperation(typeof(WellKnownController), nameof(WellKnownController.GetJwks));

        operation.Security.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task BuildTimeAndRuntimeDocuments_PublishTheSameSchemes()
    {
        var runtimeDocument = new Microsoft.OpenApi.OpenApiDocument();
        await new SecuritySchemeDocumentTransformer().TransformAsync(
            runtimeDocument,
            new OpenApiDocumentTransformerContext
            {
                DocumentName = "nocturne",
                DescriptionGroups = [],
                ApplicationServices = new ServiceCollection().BuildServiceProvider(),
            },
            CancellationToken.None);

        runtimeDocument.Components!.SecuritySchemes!.Keys
            .Should().BeEquivalentTo(DocumentWithSchemes().Components.SecuritySchemes.Keys);
    }
}
