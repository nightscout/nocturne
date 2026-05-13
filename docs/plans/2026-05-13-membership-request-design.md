# Membership Request Flow

## Problem

Anonymous guest link users see "Guest" in the sidebar with a dropdown that links to Account and Settings — pages that make no sense for an anonymous session. Instead, guests should have a path to request real membership to the tenant they're viewing.

Additionally, tenants should be able to enable a public "Request Membership" option on their sign-in page for unauthenticated visitors.

## Design

### User Flows

**Flow A: Guest link user requests membership**

1. Guest is viewing data via a guest link. The sidebar `UserMenu` dropdown shows "Request Membership" and "Leave" (no Account/Settings).
2. Guest clicks "Request Membership" → a dialog opens with a freeform text field: *"Introduce yourself to the site owner"* (max 500 chars).
3. On submit, the message is stored in `localStorage` keyed by tenant slug. The guest is redirected to `/auth/login`.
4. After account creation (passkey/OAuth), the user is redirected back to the tenant. The layout detects the stored message + the user is authenticated but not a tenant member → auto-submits `POST /api/v4/membership-requests` with the message.
5. The `localStorage` entry is cleared. A toast confirms: *"Membership request sent."*
6. The guest link session remains valid — the user can continue viewing as a guest while their request is pending.
7. All users with `members.manage` permission receive an in-app notification: *"[Name] requested membership: [message]"* — clicking navigates to `/settings/members`.

**Flow B: Unauthenticated visitor requests membership (public sign-up)**

1. Tenant has `AllowAccessRequests = true` (already exists on `TenantEntity`).
2. The sign-in page shows a "Request membership" link beneath the login options.
3. Clicking it opens the same freeform text dialog.
4. On submit, message stored in `localStorage`, user redirected to sign up.
5. After account creation, same auto-submit flow as Flow A.

**Approval/Denial:**

1. On the Members settings page, a new "Pending Requests" section lists requests: requester name, avatar, message, timestamp.
2. Each request has a role multi-select picker and "Approve" / "Deny" buttons.
3. Approve: creates `TenantMember` with selected roles via `ITenantService.AddMemberAsync()`. Requester receives `membership.approved` notification.
4. Deny: marks request as denied. Requester receives `membership.denied` notification. A denied user can re-request later (new row).

### Data Model

**New entity: `MembershipRequestEntity`**

Table: `membership_requests` (tenant-scoped, covered by RLS)

| Column | Type | Notes |
|--------|------|-------|
| `id` | UUID v7 | PK |
| `tenant_id` | UUID | FK to tenants, RLS policy column |
| `subject_id` | UUID | FK to subjects (the requester) |
| `message` | text (max 500) | Freeform intro message |
| `status` | text (max 20) | `pending`, `approved`, `denied` |
| `decided_by_subject_id` | UUID? | Who approved/denied |
| `decided_at` | timestamptz? | When the decision was made |
| `role_ids` | jsonb? | Roles assigned on approval |
| `created_at` | timestamptz | Request timestamp |

**Constraints:**
- Partial unique index on `(tenant_id, subject_id) WHERE status = 'pending'` — one pending request per user per tenant.
- Denied users can create a new request (new row).

### API

**New controller: `MembershipRequestController`** at `/api/v4/membership-requests`

| Method | Path | Auth | Permission | Purpose |
|--------|------|------|------------|---------|
| `POST` | `/` | Authenticated, non-member | None | Submit request `{ message }` |
| `GET` | `/mine` | Authenticated | None | Check current user's request status for this tenant |
| `GET` | `/` | Member | `members.manage` | List pending requests |
| `POST` | `/{id}/approve` | Member | `members.manage` | Approve with `{ roleIds }` |
| `POST` | `/{id}/deny` | Member | `members.manage` | Deny request |

The `POST /` and `GET /mine` endpoints need `[TenantlessAllowed]` or equivalent since the requester is not a tenant member and cannot pass `MemberScopeMiddleware` permission checks.

### Backend Service

**`MembershipRequestService`**

- `CreateRequestAsync(tenantId, subjectId, message)` — validates no existing pending request, creates row, sends `membership.requested` notification to all `members.manage` holders.
- `GetPendingRequestsAsync(tenantId)` — returns pending requests with subject name/avatar.
- `GetMyRequestAsync(tenantId, subjectId)` — returns current user's request status.
- `ApproveRequestAsync(requestId, tenantId, roleIds, decidedBySubjectId)` — sets status to approved, creates `TenantMember` via `ITenantService.AddMemberAsync()`, notifies requester.
- `DenyRequestAsync(requestId, tenantId, decidedBySubjectId)` — sets status to denied, notifies requester.

### Notifications

Uses existing `IInAppNotificationService` and `ISignalRBroadcastService`.

| Type | Recipients | Icon | Category | Urgency |
|------|-----------|------|----------|---------|
| `membership.requested` | All `members.manage` holders in tenant | `UserPlus` | `Action` | `Info` |
| `membership.approved` | Requester | `CheckCircle` | `Informational` | `Info` |
| `membership.denied` | Requester | `XCircle` | `Informational` | `Info` |

The `membership.requested` notification clicks through to `/settings/members`.

### Frontend Changes

**`UserMenu.svelte`:** Add `isGuestSession` prop. When true:
- Replace Account/Settings items with "Request Membership" (opens dialog) and "Leave" (clears guest session).
- No roles display.

**New `RequestMembershipDialog.svelte`:** Modal with textarea (max 500 chars), "Continue" button. Stores message in `localStorage` under key `nocturne-membership-request:{tenantSlug}`, then redirects to `/auth/login`.

**Post-login auto-submit:** In the authenticated layout, on mount: if user is authenticated but not a tenant member, check `localStorage` for a stored message. If found, call `POST /api/v4/membership-requests`, clear storage, show toast.

**Sign-in page:** When `AllowAccessRequests` is true for the tenant, show a "Request membership" link below login options. Same dialog → same `localStorage` → same post-login auto-submit.

**Members settings page:** New "Pending Requests" section (card list) above existing member/invite sections. Each row shows: avatar, name, message, timestamp, role multi-select, Approve/Deny buttons. Uses `[RemoteQuery]`/`[RemoteCommand]` for the endpoints.

### Tenant Setting

The existing `TenantEntity.AllowAccessRequests` column (defaults to `true`) controls whether the sign-in page shows the "Request membership" link. Exposed via `tenant.settings` permission for management. The guest link sidebar option is always available regardless of this setting (the guest already has access via the link).
