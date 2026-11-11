---
displayName: Codebase tour
standalone: true
---
Where things live in the Nocturne repository, for someone who wants to read or change the code behind this API.

Nocturne is a .NET 10 rewrite of the Nightscout API with a SvelteKit web app. It follows Clean Architecture: the domain has no dependency on the web framework or the database, and each outer layer depends only on the layers inside it.

## Repository layout

| Path | What it holds |
| --- | --- |
| `src/API/Nocturne.API` | The ASP.NET Core API. Controllers for v1, v2, v3 and v4, middleware, and the OpenAPI setup that produces this reference. |
| `src/Core/Nocturne.Core.Models` | Domain models. The JSON shapes in this reference come from here. |
| `src/Core/Nocturne.Core.Contracts` | Service interfaces. |
| `src/Core/Nocturne.Core.Clinical` | Clinical calculators: statistics, insulin and carbs on board, sleep. |
| `src/Infrastructure` | EF Core data access, migrations, caching and security. |
| `src/Connectors` | One project per data source (Dexcom, Glooko, Libre, CareLink and others). |
| `src/Services` | Background services, such as demo data generation. |
| `src/Aspire` | The .NET Aspire AppHost that starts the whole stack for development. |
| `src/Web/packages/app` | The SvelteKit web app. |
| `src/Web/packages/portal` | The documentation and marketing site. |
| `src/Web/packages/bridge` | A SignalR to Socket.IO bridge for legacy Nightscout clients. |
| `crates/` | The Rust alert evaluation engine, loaded by the API as a native library. |
| `sdk/` | Client SDK build files for each language. |
| `tests/` | Unit and integration tests. The folders mirror `src/`. |
| `e2e/` | End-to-end tests that run the production images. |
| `docs/api-descriptions` | The markdown source of the pages in this reference. |

## Find the code behind an endpoint

1. Take the path of the operation from this reference.
2. Search `src/API/Nocturne.API/Controllers` for the route. There is one folder per API version, and v4 has one subfolder per area (`Glucose`, `Treatments`, `Identity`, and so on).
3. The controller calls a service interface from `Nocturne.Core.Contracts`. The implementation is in the `Services` folder of the API project or in `src/Infrastructure`.
4. The service reads and writes through a repository in `Nocturne.Infrastructure.Data`, which maps database entities to the domain models.

## Rules that shape the code

- **The backend is the source of truth.** The API computes calculations, categories and chart colors. The web app only renders them.
- **Time is stored as Unix milliseconds.** Other date fields on a model are computed from that value.
- **The database isolates each tenant.** PostgreSQL Row Level Security enforces it, so a query that has no tenant filter still returns only the rows of that tenant.
- **Legacy data has no tables of its own.** A v1, v2 or v3 write is decomposed into v4 records, and a legacy read is projected back from them.
- **The TypeScript client is generated.** See [Type safety pipeline](#nocturne/tag/arch-type-safety).

## Project dependencies

The layers depend inward. `API` and `Infrastructure` depend on `Core`, and `Core` depends on nothing outside itself.

<!-- diagram: project-dependencies.mmd -->

## Run it

```bash
git clone https://github.com/nightscout/nocturne
cd nocturne
git submodule update --init
aspire start
```

`aspire start` runs PostgreSQL, the API, the web app and a gateway, and seeds a `dev` tenant with 30 days of sample data. `CLAUDE.md` and `AGENTS.md` in the repository root list the build, test and code generation commands.
