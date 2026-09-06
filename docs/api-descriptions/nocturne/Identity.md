Multi-tenancy, membership, roles, guest access, and cross-platform identity linking.

- **My Tenants** — List tenants the authenticated user belongs to.
- **My Permissions** — Effective permissions for the current tenant, computed from roles intersected with token scopes.
- **Roles** — RBAC role and permission management.
- **Member Invites** — Invite links, member listing, and role assignment.
- **Guest Links** — Temporary 48-hour read-only access links for data sharing. Recipients activate a short code to receive a scoped session cookie.
- **Connected Apps** — OAuth app grants ("connected apps") for the authenticated user.
- **Linked Platforms** — Cross-platform identity linking for the authenticated user.
- **Chat Identity** — Tenant-scoped linking of chat platform accounts (Discord, Telegram, etc.).
- **Chat Identity Directory** — Cross-tenant directory for routing chat platform identities to the correct tenant. Server-to-server only.

> **Footgun:** The Chat Identity Directory operates cross-tenant and is authenticated by instance key, not user tokens. Do not expose it to end users.
