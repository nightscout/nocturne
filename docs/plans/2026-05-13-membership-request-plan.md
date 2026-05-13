# Membership Request Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow anonymous guest-link users and unauthenticated visitors to request membership to a tenant, with admin approval/denial and in-app notifications.

**Architecture:** New `MembershipRequestEntity` + service + controller on the backend. Frontend: modified `UserMenu` for guests, new `RequestMembershipDialog`, new "Pending Requests" section on Members page, `localStorage` bridge for pre-auth message persistence.

**Tech Stack:** .NET 10, EF Core, PostgreSQL, SvelteKit 2 / Svelte 5, shadcn-svelte, SignalR (existing notification infra)

**Design doc:** `docs/plans/2026-05-13-membership-request-design.md`

---

### Task 1: Entity — MembershipRequestEntity

**Files:**
- Create: `src/Infrastructure/Nocturne.Infrastructure.Data/Entities/MembershipRequestEntity.cs`
- Modify: `src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs` (add DbSet)

**Step 1: Create the entity**

Follow the pattern from `MemberInviteEntity.cs`. The entity implements `ITenantScoped` so RLS query filters are applied automatically by `ConfigureTenantFilters`.

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A request from an authenticated user to join a tenant they are not yet a member of.
/// </summary>
[Table("membership_requests")]
public class MembershipRequestEntity : ITenantScoped
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// The subject (user) requesting membership.
    /// </summary>
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Freeform message from the requester identifying themselves to the tenant owner.
    /// </summary>
    [Column("message")]
    [MaxLength(500)]
    public string? Message { get; set; }

    /// <summary>
    /// Current status: pending, approved, denied.
    /// </summary>
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>
    /// The subject who approved or denied this request.
    /// </summary>
    [Column("decided_by_subject_id")]
    public Guid? DecidedBySubjectId { get; set; }

    /// <summary>
    /// When the decision was made.
    /// </summary>
    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// Role IDs assigned on approval.
    /// </summary>
    [Column("role_ids", TypeName = "jsonb")]
    public List<Guid>? RoleIds { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

**Step 2: Add DbSet to NocturneDbContext**

Find the other `DbSet` declarations (around line 258) and add:

```csharp
public DbSet<MembershipRequestEntity> MembershipRequests { get; set; } = null!;
```

**Step 3: Add partial unique index in OnModelCreating**

Find the `OnModelCreating` method and add configuration for the partial unique index:

```csharp
modelBuilder.Entity<MembershipRequestEntity>(entity =>
{
    entity.HasIndex(e => new { e.TenantId, e.SubjectId })
        .HasFilter("status = 'pending'")
        .IsUnique();
});
```

**Step 4: Generate EF migration**

```bash
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add AddMembershipRequests -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API
```

Review the generated migration to verify it creates the `membership_requests` table with the partial unique index.

**Step 5: Commit**

```bash
git add src/Infrastructure/Nocturne.Infrastructure.Data/Entities/MembershipRequestEntity.cs
git add src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs
git add src/Infrastructure/Nocturne.Infrastructure.Data/Migrations/
git commit -m "feat: add MembershipRequestEntity with EF migration"
```

---

### Task 2: Service — MembershipRequestService

**Files:**
- Create: `src/Core/Nocturne.Core.Contracts/Identity/IMembershipRequestService.cs`
- Create: `src/API/Nocturne.API/Services/Identity/MembershipRequestService.cs`
- Modify: `src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs` (register DI)

**Step 1: Create the service interface**

```csharp
namespace Nocturne.Core.Contracts.Identity;

public record MembershipRequestDto(
    Guid Id,
    Guid SubjectId,
    string? SubjectName,
    string? AvatarUrl,
    string? Message,
    string Status,
    DateTime CreatedAt);

public record CreateMembershipRequestResult(bool Success, string? Error);
public record DecideMembershipRequestResult(bool Success, string? Error);

public interface IMembershipRequestService
{
    Task<CreateMembershipRequestResult> CreateRequestAsync(
        Guid tenantId, Guid subjectId, string? message, CancellationToken ct = default);

    Task<MembershipRequestDto?> GetMyRequestAsync(
        Guid tenantId, Guid subjectId, CancellationToken ct = default);

    Task<List<MembershipRequestDto>> GetPendingRequestsAsync(
        Guid tenantId, CancellationToken ct = default);

    Task<DecideMembershipRequestResult> ApproveRequestAsync(
        Guid requestId, Guid tenantId, List<Guid> roleIds,
        Guid decidedBySubjectId, CancellationToken ct = default);

    Task<DecideMembershipRequestResult> DenyRequestAsync(
        Guid requestId, Guid tenantId,
        Guid decidedBySubjectId, CancellationToken ct = default);
}
```

**Step 2: Implement the service**

Model after `MemberInviteService.cs`. Key behaviors:

- `CreateRequestAsync`: Check for existing pending request (return error if found). Insert row. Look up all tenant members with `members.manage` permission. Send `membership.requested` notification to each via `IInAppNotificationService.CreateNotificationAsync`.
- `GetMyRequestAsync`: Query by `(tenantId, subjectId)` ordered by `CreatedAt` desc, return the most recent.
- `GetPendingRequestsAsync`: Query `status = "pending"` for the tenant. Join to `SubjectEntity` for name/avatar.
- `ApproveRequestAsync`: Load request, verify `status == "pending"`. Set `status = "approved"`, `DecidedBySubjectId`, `DecidedAt`, `RoleIds`. Call `ITenantService.AddMemberAsync()` to create the tenant membership. Send `membership.approved` notification to the requester.
- `DenyRequestAsync`: Load request, verify `status == "pending"`. Set `status = "denied"`, `DecidedBySubjectId`, `DecidedAt`. Send `membership.denied` notification to the requester.

Constructor dependencies:
```csharp
public MembershipRequestService(
    NocturneDbContext dbContext,
    ITenantService tenantService,
    IInAppNotificationService notificationService,
    ILogger<MembershipRequestService> logger)
```

**Step 3: Register in DI**

In `ServiceRegistrationExtensions.cs`, add alongside other identity services:
```csharp
services.AddScoped<IMembershipRequestService, MembershipRequestService>();
```

**Step 4: Commit**

```bash
git add src/Core/Nocturne.Core.Contracts/Identity/IMembershipRequestService.cs
git add src/API/Nocturne.API/Services/Identity/MembershipRequestService.cs
git add src/API/Nocturne.API/Extensions/ServiceRegistrationExtensions.cs
git commit -m "feat: add MembershipRequestService with create/approve/deny"
```

---

### Task 3: Notification templates

**Files:**
- Modify: `src/API/Nocturne.API/Services/NotificationTemplates/BuiltInNotificationTemplates.cs`

**Step 1: Register the three notification types**

Add to the `AddBuiltInTemplates` method, following the existing pattern:

```csharp
registry.Register(new NotificationTemplate
{
    Type = "membership.requested",
    Category = NotificationCategory.ActionRequired,
    DefaultUrgency = NotificationUrgency.Info,
    Icon = "user-plus",
    Source = "membership"
});

registry.Register(new NotificationTemplate
{
    Type = "membership.approved",
    Category = NotificationCategory.Informational,
    DefaultUrgency = NotificationUrgency.Info,
    Icon = "check-circle",
    Source = "membership"
});

registry.Register(new NotificationTemplate
{
    Type = "membership.denied",
    Category = NotificationCategory.Informational,
    DefaultUrgency = NotificationUrgency.Info,
    Icon = "x-circle",
    Source = "membership"
});
```

**Step 2: Commit**

```bash
git add src/API/Nocturne.API/Services/NotificationTemplates/BuiltInNotificationTemplates.cs
git commit -m "feat: register membership notification templates"
```

---

### Task 4: Controller — MembershipRequestController

**Files:**
- Create: `src/API/Nocturne.API/Controllers/V4/Identity/MembershipRequestController.cs`

**Step 1: Create the controller**

Follow the pattern from `AccessRequestController.cs` (primary constructor, `[RemoteQuery]`/`[RemoteCommand]` attributes).

Key design decisions:
- Route: `api/v4/membership-requests`
- `POST /` and `GET /mine` — require `[Authorize]` but NO permission check (the user is authenticated but may not be a tenant member). The `MemberScopeMiddleware` passes through non-members (line 129-134), so these endpoints will work.
- `GET /` — require `members.manage` permission (check via `AuthContext` or `PermissionTrie`).
- `POST /{id}/approve` and `POST /{id}/deny` — require `members.manage`.

Access tenant ID via `ITenantAccessor`:
```csharp
var tenantContext = tenantAccessor.Context;
var tenantId = tenantContext.TenantId;
```

Access subject ID from `AuthContext`:
```csharp
var authContext = HttpContext.GetAuthContext();
var subjectId = authContext.SubjectId!.Value;
```

Permission checks — use the same pattern as `MemberInviteController`:
```csharp
var trie = HttpContext.Items["PermissionTrie"] as PermissionTrie;
if (trie is null || !trie.Check("members.manage"))
    return Forbid();
```

**Endpoints:**

```csharp
[HttpPost]
[Authorize]
[RemoteCommand]
public async Task<IActionResult> CreateRequest([FromBody] CreateMembershipRequestRequest request)
// Body: { message?: string }
// Calls service.CreateRequestAsync(tenantId, subjectId, request.Message)

[HttpGet("mine")]
[Authorize]
[RemoteQuery]
public async Task<IActionResult> GetMyRequest()
// Calls service.GetMyRequestAsync(tenantId, subjectId)

[HttpGet]
[Authorize]
[RemoteQuery]
public async Task<IActionResult> GetPendingRequests()
// Permission check: members.manage
// Calls service.GetPendingRequestsAsync(tenantId)

[HttpPost("{id:guid}/approve")]
[Authorize]
[RemoteCommand]
public async Task<IActionResult> ApproveRequest(Guid id, [FromBody] ApproveMembershipRequestRequest request)
// Permission check: members.manage
// Body: { roleIds: string[] }
// Calls service.ApproveRequestAsync(id, tenantId, roleIds, decidedBySubjectId)

[HttpPost("{id:guid}/deny")]
[Authorize]
[RemoteCommand]
public async Task<IActionResult> DenyRequest(Guid id)
// Permission check: members.manage
// Calls service.DenyRequestAsync(id, tenantId, decidedBySubjectId)
```

**Step 2: Commit**

```bash
git add src/API/Nocturne.API/Controllers/V4/Identity/MembershipRequestController.cs
git commit -m "feat: add MembershipRequestController with CRUD endpoints"
```

---

### Task 5: Build & regenerate API client

**Step 1: Build and verify**

```bash
dotnet build
```

Fix any compilation errors.

**Step 2: Regenerate the NSwag TypeScript client**

```bash
dotnet build -t:GenerateClient src/API/Nocturne.API/Nocturne.API.csproj
```

This regenerates the generated remote functions in `src/Web/packages/app/src/lib/api/generated/`. Verify new files appear for `membershipRequests.generated.remote.ts` (or similar — the name derives from the controller name).

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/lib/api/generated/
git commit -m "chore: regenerate API client with membership request endpoints"
```

---

### Task 6: Frontend — UserMenu guest mode changes

**Files:**
- Modify: `src/Web/packages/app/src/lib/components/layout/UserMenu.svelte`
- Modify: `src/Web/packages/app/src/lib/components/layout/AppSidebar.svelte` (pass `isGuestSession` to UserMenu)

**Step 1: Add `isGuestSession` prop to UserMenu**

Add to the Props interface (around line 9):
```typescript
/** Whether the current session is a guest link session */
isGuestSession?: boolean;
```

Destructure it (around line 19):
```typescript
const { user, collapsed = false, class: className = "", isPlatformAdmin = false, isGuestSession = false }: Props = $props();
```

**Step 2: Conditionally render guest vs member dropdown items**

In the `DropdownMenu.Content`, after the roles section (around line 108), wrap the existing Account/Settings items in an `{#if !isGuestSession}` block. Add an `{:else}` block with:

- "Request Membership" item (with `UserPlus` icon) — dispatches a custom event or calls a callback
- "Leave" item (with `LogOut` icon) — navigates to guest logout

```svelte
{#if !isGuestSession}
  <DropdownMenu.Group>
    <DropdownMenu.Item onSelect={() => goto("/settings/account")}>
      <User class="mr-2 h-4 w-4" />
      <span>Account</span>
    </DropdownMenu.Item>
    <!-- ... existing Settings/Admin items ... -->
  </DropdownMenu.Group>
{:else}
  <DropdownMenu.Group>
    <DropdownMenu.Item onSelect={() => (showRequestDialog = true)}>
      <UserPlus class="mr-2 h-4 w-4" />
      <span>Request Membership</span>
    </DropdownMenu.Item>
  </DropdownMenu.Group>
{/if}
```

For the "Log out" item at the bottom, keep it — it clears the guest session.

**Step 3: Pass `isGuestSession` from AppSidebar to UserMenu**

In `AppSidebar.svelte` around line 527:
```svelte
<UserMenu
  {user}
  {isPlatformAdmin}
  {isGuestSession}
  collapsed={sidebar.state === "collapsed"}
  class="flex-1 min-w-0"
/>
```

**Step 4: Commit**

```bash
git add src/Web/packages/app/src/lib/components/layout/UserMenu.svelte
git add src/Web/packages/app/src/lib/components/layout/AppSidebar.svelte
git commit -m "feat: show 'Request Membership' for guest sessions in UserMenu"
```

---

### Task 7: Frontend — RequestMembershipDialog

**Files:**
- Create: `src/Web/packages/app/src/lib/components/members/RequestMembershipDialog.svelte`
- Modify: `src/Web/packages/app/src/lib/components/layout/UserMenu.svelte` (integrate dialog)

**Step 1: Create the dialog component**

Follow the dialog pattern from `TotpSetupDialog.svelte`. Uses shadcn-svelte `Dialog` component.

```svelte
<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";

  interface Props {
    open: boolean;
    tenantSlug?: string;
  }

  let { open = $bindable(false), tenantSlug }: Props = $props();

  let message = $state("");

  const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

  function handleSubmit() {
    if (!browser || !tenantSlug) return;

    // Store message in localStorage for post-login auto-submit
    try {
      localStorage.setItem(`${STORAGE_KEY_PREFIX}${tenantSlug}`, message);
    } catch {
      // Storage full or unavailable — proceed anyway
    }

    open = false;

    // Redirect to sign up with return URL
    const returnUrl = encodeURIComponent(window.location.pathname);
    goto(`/auth/login?returnUrl=${returnUrl}`);
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Request Membership</Dialog.Title>
      <Dialog.Description>
        Introduce yourself to the site owner so they know who you are.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <Textarea
        bind:value={message}
        placeholder="e.g. I'm Sarah's endocrinologist"
        maxlength={500}
        rows={3}
      />
      <p class="text-xs text-muted-foreground text-right">
        {message.length}/500
      </p>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
      <Button onclick={handleSubmit}>Continue to Sign Up</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
```

**Step 2: Integrate into UserMenu**

Import the dialog and add state + rendering in `UserMenu.svelte`:

```svelte
<script>
  import RequestMembershipDialog from "$lib/components/members/RequestMembershipDialog.svelte";
  // ...
  let showRequestDialog = $state(false);
</script>

<!-- After the DropdownMenu.Root closing tag -->
{#if isGuestSession}
  <RequestMembershipDialog bind:open={showRequestDialog} tenantSlug={/* derive from hostname */} />
{/if}
```

The `tenantSlug` can be derived from `window.location.hostname` the same way `AppSidebar` does it (line 104: `currentSlug = parts[0]`). Pass it as a prop or derive it in UserMenu.

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/lib/components/members/RequestMembershipDialog.svelte
git add src/Web/packages/app/src/lib/components/layout/UserMenu.svelte
git commit -m "feat: add RequestMembershipDialog with localStorage bridge"
```

---

### Task 8: Frontend — Post-login auto-submit

**Files:**
- Create: `src/Web/packages/app/src/lib/components/members/MembershipRequestAutoSubmit.svelte`
- Modify: `src/Web/packages/app/src/routes/(authenticated)/+layout.svelte` (mount the component)

**Step 1: Create the auto-submit component**

This component runs on mount: checks `localStorage` for a stored membership request message, and if the user is authenticated but not a tenant member, submits the request to the API and clears storage.

```svelte
<script lang="ts">
  import { browser } from "$app/environment";
  import { onMount } from "svelte";
  // Import the generated remote command for creating a membership request
  // (exact name depends on NSwag output — likely createRequest from membershipRequests.generated.remote)

  interface Props {
    isAuthenticated: boolean;
    isGuestSession: boolean;
    isMember: boolean;
    tenantSlug: string | null;
  }

  const { isAuthenticated, isGuestSession, isMember, tenantSlug }: Props = $props();

  const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

  onMount(async () => {
    if (!browser || !tenantSlug || !isAuthenticated || isGuestSession || isMember) return;

    const key = `${STORAGE_KEY_PREFIX}${tenantSlug}`;
    const message = localStorage.getItem(key);
    if (!message && message !== "") return;

    try {
      // Call the generated remote command
      await createRequest({ request: { message: message || undefined } });
      localStorage.removeItem(key);
      // Show success feedback (inline message or notification)
    } catch {
      // Silently fail — user can retry manually
      localStorage.removeItem(key);
    }
  });
</script>
```

**Step 2: Mount in the authenticated layout**

In `+layout.svelte`, import and render the component. It needs to know whether the user is a member — this can be derived from `effectivePermissions` being non-empty (non-members have no permissions) or a new `isMember` field from the layout server.

The simplest approach: add `isMember` to the layout server data by checking if the user has any effective permissions or membership status. Alternatively, the auto-submit component can call `GET /api/v4/membership-requests/mine` to check status before submitting.

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/lib/components/members/MembershipRequestAutoSubmit.svelte
git add src/Web/packages/app/src/routes/(authenticated)/+layout.svelte
git commit -m "feat: auto-submit membership request after login"
```

---

### Task 9: Frontend — Pending Requests section on Members page

**Files:**
- Create: `src/Web/packages/app/src/lib/components/members/PendingRequestsList.svelte`
- Modify: `src/Web/packages/app/src/routes/(authenticated)/settings/members/+page.svelte`

**Step 1: Create PendingRequestsList component**

Follow the pattern of `PendingInvitesList.svelte` and `MemberCard.svelte`. Displays a list of pending membership requests with:
- Requester avatar, name, message, timestamp
- Role multi-select picker (reuse the checkbox pattern from `CreateInviteCard.svelte`)
- "Approve" and "Deny" buttons

```svelte
<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Avatar from "$lib/components/ui/avatar";
  import { Button } from "$lib/components/ui/button";
  import { Check, X, UserPlus } from "lucide-svelte";
  // Import generated remote functions for approve/deny
  // Import role type from generated client

  interface PendingRequest {
    id: string;
    subjectId: string;
    subjectName?: string;
    avatarUrl?: string;
    message?: string;
    createdAt: string;
  }

  interface Props {
    requests: PendingRequest[];
    roles: { id: string; name: string }[];
    onApprove: (requestId: string, roleIds: string[]) => Promise<void>;
    onDeny: (requestId: string) => Promise<void>;
  }

  const { requests, roles, onApprove, onDeny }: Props = $props();

  // Per-request role selection state
  let selectedRoles = $state<Record<string, string[]>>({});

  function toggleRole(requestId: string, roleId: string) {
    const current = selectedRoles[requestId] ?? [];
    selectedRoles[requestId] = current.includes(roleId)
      ? current.filter((r) => r !== roleId)
      : [...current, roleId];
  }
</script>
```

Render each request as a card with role checkboxes and action buttons.

**Step 2: Integrate into Members page**

In `+page.svelte`, import and query pending requests using the generated remote function. Add a section above the existing members list (conditionally shown when `canManageMembers`):

```svelte
{#if canManageMembers && pendingRequests.length > 0}
  <Card.Root>
    <Card.Header>
      <Card.Title class="flex items-center gap-2">
        <UserPlus class="h-5 w-5" />
        Pending Requests
      </Card.Title>
    </Card.Header>
    <Card.Content>
      <PendingRequestsList
        requests={pendingRequests}
        roles={allRoles}
        onApprove={handleApproveRequest}
        onDeny={handleDenyRequest}
      />
    </Card.Content>
  </Card.Root>
{/if}
```

Wire up `handleApproveRequest` and `handleDenyRequest` to call the generated remote commands, show success/error messages, and refresh the queries.

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/lib/components/members/PendingRequestsList.svelte
git add src/Web/packages/app/src/routes/(authenticated)/settings/members/+page.svelte
git commit -m "feat: add Pending Requests section to Members settings page"
```

---

### Task 10: Frontend — Sign-in page "Request Membership" link

**Files:**
- Modify: `src/Web/packages/app/src/routes/(unauthenticated)/auth/login/+page.svelte`
- Modify: `src/Web/packages/app/src/routes/(authenticated)/+layout.server.ts` (expose `allowAccessRequests`)

**Step 1: Expose `allowAccessRequests` to the frontend**

The `TenantEntity.AllowAccessRequests` field needs to reach the login page. The site security probe already fetches tenant status — check if `allowAccessRequests` is in the status response. If not, add it to the status endpoint response or fetch it separately.

The simplest path: add `allowAccessRequests` to `locals` during the site security handle (it already queries status), then pass it through layout data.

In `+layout.server.ts`, add to the return:
```typescript
return {
  // ... existing fields ...
  allowAccessRequests: locals.allowAccessRequests ?? false,
};
```

**Step 2: Add link to sign-in page**

In the login page's `Card.Footer`, add a conditional link:

```svelte
{#if data.allowAccessRequests}
  <div class="text-center">
    <button
      class="text-sm text-muted-foreground hover:text-foreground underline"
      onclick={() => (showRequestDialog = true)}
    >
      Request membership
    </button>
  </div>
{/if}

<RequestMembershipDialog bind:open={showRequestDialog} tenantSlug={/* from hostname */} />
```

This reuses the same `RequestMembershipDialog` component from Task 7.

**Step 3: Commit**

```bash
git add src/Web/packages/app/src/routes/(unauthenticated)/auth/login/+page.svelte
git add src/Web/packages/app/src/routes/(authenticated)/+layout.server.ts
git commit -m "feat: show 'Request membership' on sign-in page when enabled"
```

---

### Task 11: Unit tests

**Files:**
- Create: `tests/Unit/Nocturne.API.Tests/Services/Identity/MembershipRequestServiceTests.cs`

**Step 1: Test create request**

- Happy path: creates request, returns success
- Duplicate pending request: returns error
- Sends notifications to members.manage holders

**Step 2: Test approve request**

- Happy path: sets status to approved, creates tenant member with roles, sends notification
- Request not found: returns error
- Request already decided: returns error

**Step 3: Test deny request**

- Happy path: sets status to denied, sends notification
- Request not found: returns error

**Step 4: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~MembershipRequestServiceTests"
```

**Step 5: Commit**

```bash
git add tests/Unit/Nocturne.API.Tests/Services/Identity/MembershipRequestServiceTests.cs
git commit -m "test: add MembershipRequestService unit tests"
```

---

### Task 12: Frontend type-check & integration test

**Step 1: Run frontend type checking**

```bash
cd src/Web/packages/app && pnpm run check
```

Fix any TypeScript errors.

**Step 2: Manual integration test checklist**

1. Start Aspire: `aspire start`
2. Create a guest link from Settings > Members
3. Open guest link in incognito — verify "Request Membership" appears in sidebar dropdown (not Account/Settings)
4. Click "Request Membership" — verify dialog appears with text field
5. Submit — verify redirect to login page
6. Create account via passkey/OAuth
7. Verify membership request auto-submits after login
8. Switch to owner account — verify notification bell shows new notification
9. Navigate to Settings > Members — verify "Pending Requests" section shows the request
10. Approve with roles — verify requester becomes a member
11. Test deny flow similarly
12. Test sign-in page "Request membership" link (with `AllowAccessRequests = true`)

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: address issues found during integration testing"
```
