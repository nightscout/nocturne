var builder = WebApplication.CreateBuilder(args);

// Add service defaults
builder.AddServiceDefaults();

// Add NSwag OpenAPI services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument(config =>
{
    config.Title = "Nocturne Portal API";
    config.Version = "v1";
    config.Description = "Configuration generator and documentation portal for Nocturne";
});

// Add services
builder.Services.AddSingleton<Nocturne.Portal.API.Services.DockerComposeGenerator>();

// Add controllers
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseOpenApi();
    app.UseSwaggerUi();
}

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();
