---
displayName: Deployment topology
standalone: true
---
The services of a published Nocturne instance and how traffic reaches them. Both diagrams are generated from the Aspire publish output, so they match the Docker Compose bundle attached to each release.

## Services

<!-- diagram: published-services.mmd -->

| Service | Role |
| --- | --- |
| `caddy` | Terminates TLS and passes traffic to the gateway. |
| `gateway` | The YARP reverse proxy. It routes each request by path. |
| `nocturne-api` | This API. |
| `nocturne-web` | The SvelteKit web app and its server side. |
| `nocturne-postgres-server` | PostgreSQL. Holds all tenant data. |
| `watchtower` | Updates the containers when a new image is published. |

## Routing

<!-- diagram: published-routing.mmd -->

The gateway sends these paths directly to `nocturne-api`:

- `/api/oauth` and `/api/auth/oidc`
- `/.well-known`
- `/openapi` and `/scalar`

Every other path goes to `nocturne-web`. Its server side forwards `/api` requests to `nocturne-api` and renders the app for the rest.
