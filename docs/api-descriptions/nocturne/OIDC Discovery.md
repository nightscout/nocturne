Standard `.well-known` endpoints that make Nocturne act as its own OAuth 2.0 / OIDC issuer.

Returns the OpenID Provider configuration (`openid-configuration`) and JSON Web Key Set (`jwks.json`). These endpoints are **unauthenticated** by design — they must be publicly reachable for token validation.
