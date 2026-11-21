---
displayName: Type safety pipeline
standalone: true
---
How a type defined once in .NET reaches the web app, so the frontend never declares its own copy of an API model.

<!-- diagram: type-safety-pipeline.mmd -->

## Stages

The pipeline runs as part of the API build, in three stages:

1. **NSwag** reads the controllers and writes an OpenAPI document and a typed TypeScript client.
2. **The Zod generator** reads that OpenAPI document and writes a validator for each schema.
3. **Remote function codegen** writes a SvelteKit remote function for each controller action marked `[RemoteQuery]`, `[RemoteCommand]` or `[RemoteFormData]`.

The NSwag client, its OpenAPI document and the Zod schemas go to `src/Web/packages/api-client/src/`, the `@nocturne/api-client` package that the app and the bot both import. The remote functions go to `src/Web/packages/app/src/lib/api/generated/`. Neither is committed: the build regenerates them.

## Two OpenAPI documents

The build-time document feeds code generation. It has one tag per controller, so the client has one class per controller.

The documents in this reference are produced at run time by a second pipeline, and are grouped by area for reading. A change to how this reference is grouped does not change the generated client.

## When the client is stale

The pipeline runs only when the public surface of the API changes. To force it:

```bash
dotnet build src/API/Nocturne.API/Nocturne.API.csproj -p:GenerateNSwagClient=true
```
