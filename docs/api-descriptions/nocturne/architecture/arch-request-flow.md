---
displayName: Request flow
standalone: true
---
What happens to a request between the client and the database.

<!-- diagram: request-flow.mmd -->

## Gateway

A YARP gateway is the one public HTTP endpoint of the stack. The API and the web app run as plain HTTP behind it. The gateway sends the OAuth, OIDC and `.well-known` paths, the OpenAPI documents and this reference directly to the API. Other `/api` paths go to the server side of the web app, which forwards them to the API. [Deployment topology](#nocturne/tag/arch-deployment) shows the routes.

## Middleware

Each API request passes through three steps before it reaches a controller:

1. **Tenant resolution** reads the host name and finds the tenant. A host that matches no tenant is refused, except on the few paths that need no tenant.
2. **Authentication** reads the credential (a bearer token, a session cookie, or a legacy API secret) and resolves who is calling.
3. **Member scope** combines the caller's role in the tenant with the scopes of the credential. The result is the set of permissions that the request has.

## Controller, service, repository

The controller checks permissions and validates input. A service does the work. A repository opens a database context for that one operation and closes it when the operation ends.

## Database

When a connection opens, the API writes the tenant and the state of the reader to session settings. Row Level Security policies read those settings, so the database returns only the rows that the caller may see. This applies to every query, including one that has no tenant filter in code.
