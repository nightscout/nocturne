using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
// using Microsoft.OpenApi.Models;

namespace Nocturne.API.Configuration;

// TODO: Fix after OpenAPI package version issue is resolved
/*
/// <summary>
/// Transforms the OpenAPI document to include proper metadata and documentation
/// </summary>
public class OpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        document.Info = new OpenApiInfo
        {
            Title = "Nocturne API",
            Version = "v1",
            Description =
                "Modern C# rewrite of Nightscout API with 1:1 API parity. "
                + "Provides comprehensive blood glucose monitoring endpoints for diabetes management.",
            Contact = new OpenApiContact
            {
                Name = "Nocturne Project",
                Url = new Uri("https://github.com/ryceg/nocturne"),
            },
            // License = new OpenApiLicense
            // {
            //     Name = "MIT License",
            //     Url = new Uri("https://opensource.org/licenses/MIT")
            // }
        };

        document.Servers = new List<OpenApiServer>
        {
            new OpenApiServer { Url = "/", Description = "Current server" },
        };

        // Add tags for better organization in Scalar
        var tags = new List<OpenApiTag>
        {
            new OpenApiTag { Name = "Status", Description = "Server status and health endpoints" },
            new OpenApiTag
            {
                Name = "Entries",
                Description = "Blood glucose entries (SGV, MBG, CAL)",
            },
            new OpenApiTag
            {
                Name = "Treatments",
                Description = "Treatment records (bolus, temp basal, etc.)",
            },
            new OpenApiTag { Name = "Profile", Description = "Basal profiles and settings" },
            new OpenApiTag
            {
                Name = "Device Status",
                Description = "Device status and battery information",
            },
            new OpenApiTag
            {
                Name = "Device Age",
                Description = "Device age tracking for cannula, sensor, battery, and calibration",
            },
            new OpenApiTag
            {
                Name = "Food",
                Description = "Food database and nutrition information",
            },
            new OpenApiTag { Name = "V2 API", Description = "Version 2 API endpoints" },
            new OpenApiTag { Name = "V3 API", Description = "Version 3 API endpoints" },
            new OpenApiTag
            {
                Name = "Data Processing",
                Description = "Advanced data processing and analytics",
            },
            new OpenApiTag
            {
                Name = "Authentication",
                Description = "Authentication and authorization",
            },
        };
        document.Tags = tags;

        return Task.CompletedTask;
    }
} */
