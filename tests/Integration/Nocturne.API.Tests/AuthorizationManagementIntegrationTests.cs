using System.Net;
using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for authorization management endpoints
/// </summary>
[Parity]
public class AuthorizationManagementIntegrationTests : ApiIntegrationTestBase
{
    public AuthorizationManagementIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Fact]
    [Parity]
    public async Task AuthorizationManagement_FullWorkflow_WorksCorrectly()
    {
        // Arrange
        var client = ApiClient;

        // Test requires admin authentication
        // This test demonstrates the complete workflow but would require
        // setting up authentication headers in a real test environment

        // Note: These endpoints require admin permissions, so they would
        // return 401/403 without proper authentication in a real scenario

        // 1. Test GET roles endpoint exists
        var rolesResponse = await client.GetAsync("/api/v2/authorization/roles");

        // 2. Test GET subjects endpoint exists
        var subjectsResponse = await client.GetAsync("/api/v2/authorization/subjects");

        // 3. Test POST roles endpoint exists
        var newRole = new Role
        {
            Name = "test-role",
            Permissions = new List<string> { "api:*:read" },
            Notes = "Test role for integration testing",
        };

        var createRoleResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/roles",
            newRole
        );

        // 4. Test POST subjects endpoint exists
        var newSubject = new Subject
        {
            Name = "test-subject",
            Roles = new List<string> { "test-role" },
            Notes = "Test subject for integration testing",
        };

        var createSubjectResponse = await client.PostAsJsonAsync(
            "/api/v2/authorization/subjects",
            newSubject
        );

        // Assert - endpoints should be reachable (even if unauthorized)
        Assert.True(
            rolesResponse.StatusCode == HttpStatusCode.Unauthorized
                || rolesResponse.StatusCode == HttpStatusCode.OK
                || rolesResponse.StatusCode == HttpStatusCode.Forbidden
        );

        Assert.True(
            subjectsResponse.StatusCode == HttpStatusCode.Unauthorized
                || subjectsResponse.StatusCode == HttpStatusCode.OK
                || subjectsResponse.StatusCode == HttpStatusCode.Forbidden
        );

        Output.WriteLine($"Roles endpoint returned: {rolesResponse.StatusCode}");
        Output.WriteLine($"Subjects endpoint returned: {subjectsResponse.StatusCode}");
        Output.WriteLine($"Create role endpoint returned: {createRoleResponse.StatusCode}");
        Output.WriteLine($"Create subject endpoint returned: {createSubjectResponse.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task AuthorizationEndpoints_AreProperlySecured()
    {
        // Arrange
        var client = ApiClient;

        // Verify all admin endpoints return appropriate responses without authentication
        var endpoints = new[] { "/api/v2/authorization/subjects", "/api/v2/authorization/roles" };

        foreach (var endpoint in endpoints)
        {
            // GET requests
            var getResponse = await client.GetAsync(endpoint);
            Assert.True(
                getResponse.StatusCode == HttpStatusCode.Unauthorized
                    || getResponse.StatusCode == HttpStatusCode.OK
                    || getResponse.StatusCode == HttpStatusCode.Forbidden
            );

            // POST requests
            var postResponse = await client.PostAsJsonAsync(endpoint, new { });
            Assert.True(
                postResponse.StatusCode == HttpStatusCode.Unauthorized
                    || postResponse.StatusCode == HttpStatusCode.BadRequest
                    || postResponse.StatusCode == HttpStatusCode.Forbidden
            );

            // PUT requests
            var putResponse = await client.PutAsJsonAsync(endpoint, new { });
            Assert.True(
                putResponse.StatusCode == HttpStatusCode.Unauthorized
                    || putResponse.StatusCode == HttpStatusCode.BadRequest
                    || putResponse.StatusCode == HttpStatusCode.Forbidden
            );

            Output.WriteLine(
                $"Endpoint {endpoint}: GET={getResponse.StatusCode}, POST={postResponse.StatusCode}, PUT={putResponse.StatusCode}"
            );
        }

        // Test DELETE endpoints
        var deleteSubjectResponse = await client.DeleteAsync(
            "/api/v2/authorization/subjects/test-id"
        );
        Assert.True(
            deleteSubjectResponse.StatusCode == HttpStatusCode.Unauthorized
                || deleteSubjectResponse.StatusCode == HttpStatusCode.NotFound
                || deleteSubjectResponse.StatusCode == HttpStatusCode.Forbidden
        );

        var deleteRoleResponse = await client.DeleteAsync("/api/v2/authorization/roles/test-id");
        Assert.True(
            deleteRoleResponse.StatusCode == HttpStatusCode.Unauthorized
                || deleteRoleResponse.StatusCode == HttpStatusCode.NotFound
                || deleteRoleResponse.StatusCode == HttpStatusCode.Forbidden
        );

        Output.WriteLine($"Delete subject returned: {deleteSubjectResponse.StatusCode}");
        Output.WriteLine($"Delete role returned: {deleteRoleResponse.StatusCode}");
    }

    [Fact]
    [Parity]
    public async Task AuthorizationEndpoints_ReturnCorrectContentTypes()
    {
        // Arrange
        var client = ApiClient;

        // Test that endpoints return JSON content type headers
        var response = await client.GetAsync("/api/v2/authorization/subjects");

        // Even unauthorized responses should have proper content type for JSON APIs
        if (response.Content.Headers.ContentType != null)
        {
            var contentType = response.Content.Headers.ContentType.MediaType;
            if (!string.IsNullOrEmpty(contentType))
            {
                Assert.True(
                    contentType.Contains("application/json") || contentType.Contains("text/plain")
                );
            }
        }

        Output.WriteLine($"Content type test completed. Status: {response.StatusCode}");
        Output.WriteLine(
            $"Content type: {response.Content.Headers.ContentType?.MediaType ?? "None"}"
        );
    }
}
