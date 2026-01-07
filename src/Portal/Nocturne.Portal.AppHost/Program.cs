using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Add the Portal API service
var api = builder
    .AddProject<Projects.Nocturne_Portal_API>("portal-api")
    .WithExternalHttpEndpoints();

// Add the Portal Web frontend
builder.AddPnpmApp("portal-web", "../../Web/packages/portal", scriptName: "dev")
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 5173, env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

var app = builder.Build();
app.Run();
