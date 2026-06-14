<script lang="ts">
  import { page } from "$app/state";
  import { slide } from "svelte/transition";
  import { flip } from "svelte/animate";
  import * as Card from "$lib/components/ui/card";
  import {
    Users,
    Check,
    AlertTriangle,
    Link,
    ShieldAlert,
    Globe,
    Lock,
    ScrollText,
    ChevronRight,
  } from "lucide-svelte";
  import { resolve } from "$app/paths";
  import { getCurrentTenantId } from "../current-tenant.remote";
  import { getMembers } from "$lib/api/generated/memberInvites.generated.remote";
  import {
    listInvites,
    revokeInvite,
    removeMember,
  } from "$api/generated/tenants.generated.remote";
  import { getRoles } from "$lib/api/generated/roles.generated.remote";
  import { getShareLink } from "$api/generated/shareLinks.generated.remote";
  import {
    setMemberRoles,
    setMemberPermissions,
    setMemberLimitTo24Hours,
  } from "$lib/api/generated/memberInvites.generated.remote";
  import { coachmark } from "@nocturne/coach";
  import {
    getPendingRequests,
    approveRequest,
    denyRequest,
  } from "$lib/api/generated/membershipRequests.generated.remote";
  import CreateInviteCard from "$lib/components/members/CreateInviteCard.svelte";
  import PendingInvitesList from "$lib/components/members/PendingInvitesList.svelte";
  import PendingRequestsList from "$lib/components/members/PendingRequestsList.svelte";
  import MemberCard from "$lib/components/members/MemberCard.svelte";
  import GuestLinksSection from "$lib/components/members/GuestLinksSection.svelte";
  import PublicAccessCard from "$lib/components/members/PublicAccessCard.svelte";
  import MembershipRequestsCard from "$lib/components/members/MembershipRequestsCard.svelte";
  import RolesSection from "$lib/components/members/RolesSection.svelte";

  const effectivePermissions: string[] = $derived(
    (page.data as any).effectivePermissions ?? [],
  );
  const hasStar = $derived(effectivePermissions.includes("*"));
  const canInvite = $derived(
    hasStar || effectivePermissions.includes("members.invite"),
  );
  const canManageMembers = $derived(
    hasStar ||
      effectivePermissions.includes("members.manage") ||
      effectivePermissions.includes("sharing.manage"),
  );
  const canEditMemberRoles = $derived(
    hasStar || effectivePermissions.includes("members.manage"),
  );
  const canManageSharing = $derived(
    hasStar || effectivePermissions.includes("sharing.manage"),
  );
  const canManageRoles = $derived(
    hasStar || effectivePermissions.includes("roles.manage"),
  );
  const canViewAudit = $derived(
    hasStar ||
      effectivePermissions.includes("audit.read") ||
      effectivePermissions.includes("audit.manage"),
  );
  // GuestLinksSection self-gates on this; mirror it so the access-denied card isn't shown to a
  // guest-link-only user who can still use the guest-links section.
  const canCreateGuestLinks = $derived(
    hasStar || effectivePermissions.includes("sharing.guest"),
  );

  // Tenant
  const tenantIdQuery = getCurrentTenantId();
  const tenantId = $derived(tenantIdQuery.current ?? undefined);

  // Queries
  const membersQuery = getMembers();
  const invitesQuery = $derived(tenantId ? listInvites(tenantId) : null);
  const rolesQuery = getRoles();
  const pendingRequestsQuery = $derived(canManageMembers ? getPendingRequests() : null);
  const shareQuery = $derived(canManageSharing ? getShareLink() : null);

  // Data
  const allMembers = $derived(membersQuery.current ?? []);
  const invites = $derived(invitesQuery?.current ?? []);
  const activeInvites = $derived(invites.filter((i) => i.isValid));
  const allRoles = $derived(rolesQuery.current ?? []);
  const pendingRequests = $derived(pendingRequestsQuery?.current ?? []);
  const share = $derived(shareQuery?.current ?? null);

  const publicMember = $derived(allMembers.find((m) => m.isSystemSubject));
  const sharingConfigured = $derived(
    (publicMember?.roles ?? []).length > 0 ||
      (publicMember?.directPermissions ?? []).length > 0,
  );

  // Header status chips
  const memberCount = $derived(allMembers.filter((m) => !m.isSystemSubject).length);
  const publicStatus = $derived(
    !share?.enabled
      ? "Members only"
      : share.fullHistory
        ? "Public · all history"
        : "Public · last 24h",
  );

  // --- UI state ---
  let showCreateInvite = $state(false);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);
  let removingMemberIds = $state(new Set<string>());

  // --- Member edit state ---
  let expandedMember = $state<string | null>(null);
  let isSavingMember = $state(false);
  let isRevokingInvite = $state<string | null>(null);

  /** Surface a server-provided message when present, else a generic fallback. */
  function messageFrom(e: unknown, fallback: string): string {
    return (e as { body?: { message?: string } })?.body?.message ?? fallback;
  }

  // Visible members — system subjects (e.g. Public) are managed via the
  // public access card above, not as removable/editable cards.
  const visibleMembers = $derived(
    allMembers.filter(
      (m) => !m.isSystemSubject && !removingMemberIds.has(m.subjectId!),
    ),
  );

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  function toggleExpandMember(memberId: string) {
    if (expandedMember === memberId) {
      expandedMember = null;
    } else {
      expandedMember = memberId;
    }
  }

  async function saveMemberChanges(memberId: string, roleIds: string[], permissions: string[]) {
    isSavingMember = true;
    errorMessage = null;
    try {
      await Promise.all([
        setMemberRoles({ id: memberId, request: { roleIds } }),
        setMemberPermissions({
          id: memberId,
          request: { directPermissions: permissions },
        }),
      ]);
      successMessage = "Member updated successfully.";
      expandedMember = null;
      clearMessages();
    } catch (e) {
      errorMessage = messageFrom(e, "Failed to update member. Please try again.");
      clearMessages();
    } finally {
      isSavingMember = false;
    }
  }

  async function handleApproveRequest(requestId: string, roleIds: string[]) {
    errorMessage = null;
    try {
      await approveRequest({ id: requestId, request: { roleIds } });
      successMessage = "Membership request approved.";
      clearMessages();
    } catch {
      errorMessage = "Failed to approve request. Please try again.";
      clearMessages();
    }
  }

  async function handleDenyRequest(requestId: string) {
    errorMessage = null;
    try {
      await denyRequest(requestId);
      successMessage = "Membership request denied.";
      clearMessages();
    } catch {
      errorMessage = "Failed to deny request. Please try again.";
      clearMessages();
    }
  }
</script>

<svelte:head>
  <title>Sharing & Privacy - Settings - Nocturne</title>
</svelte:head>

<div
  class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6"
  {@attach coachmark({ key: "onboarding.sharing", title: "Share with a caretaker", description: "Share your glucose data with a parent, partner, or clinician.", completedWhen: () => sharingConfigured })}
>
  <!-- Page header -->
  <div class="flex flex-col gap-4 @md:flex-row @md:items-start @md:justify-between">
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
        <Users class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Sharing &amp; Privacy</h1>
        <p class="text-muted-foreground">
          Control who can see your data, and what they can do with it.
        </p>
      </div>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      {#if canManageSharing}
        <span class="inline-flex h-8 items-center gap-2 rounded-full bg-secondary px-3 text-xs font-medium">
          {#if share?.enabled}
            <Globe class="h-3.5 w-3.5 text-green-600 dark:text-green-400" />
          {:else}
            <Lock class="h-3.5 w-3.5 text-muted-foreground" />
          {/if}
          {publicStatus}
        </span>
      {/if}
      <span class="inline-flex h-8 items-center gap-2 rounded-full bg-secondary px-3 text-xs font-medium">
        <Users class="h-3.5 w-3.5 text-muted-foreground" />
        {memberCount} member{memberCount !== 1 ? "s" : ""}
      </span>
    </div>
  </div>

  {#if errorMessage}
    <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20">
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">{successMessage}</p>
    </div>
  {/if}

  <!-- Public access -->
  <PublicAccessCard />

  <!-- Membership requests (independent of public access) -->
  <MembershipRequestsCard />

  <!-- Members & invites -->
  {#if canManageMembers || canInvite}
    <div class="space-y-4">
      <h2 class="flex items-center gap-2 text-lg font-semibold">
        <Users class="h-5 w-5" />
        Members &amp; invites
      </h2>

      {#if canManageMembers}
      <!-- Pending Requests -->
      {#if pendingRequests.length > 0}
        <PendingRequestsList
          requests={pendingRequests}
          roles={allRoles}
          onApprove={handleApproveRequest}
          onDeny={handleDenyRequest}
        />
      {/if}

      {#if visibleMembers.length === 0 && removingMemberIds.size === 0}
        <Card.Root>
          <Card.Content class="flex flex-col items-center justify-center py-12 text-center">
            <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted">
              <Users class="h-6 w-6 text-muted-foreground" />
            </div>
            <p class="max-w-sm text-sm text-muted-foreground">
              No members. Invite someone to share your data.
            </p>
          </Card.Content>
        </Card.Root>
      {:else}
        {#each visibleMembers as member (member.subjectId)}
          <div transition:slide={{ duration: 300 }} animate:flip={{ duration: 300 }}>
            <MemberCard
              {member}
              roles={allRoles}
              canEditRoles={canEditMemberRoles}
              canManage={true}
              currentSubjectId={page.data.user?.subjectId}
              isExpanded={expandedMember === member.subjectId}
              isSaving={isSavingMember}
              onToggleExpand={() => toggleExpandMember(member.subjectId!)}
              onSaveRoles={(roleIds, permissions) =>
                saveMemberChanges(member.id!, roleIds, permissions)}
              onSaveLimitTo24Hours={async (limitTo24Hours) => {
                try {
                  await setMemberLimitTo24Hours({
                    id: member.id!,
                    request: { limitTo24Hours },
                  });
                } catch (e) {
                  errorMessage = messageFrom(e, "Failed to update member. Please try again.");
                  clearMessages();
                }
              }}
              onRemove={async () => {
                if (!tenantId || !member.subjectId) return;
                removingMemberIds = new Set([...removingMemberIds, member.subjectId]);
                errorMessage = null;
                try {
                  await removeMember({ id: tenantId, subjectId: member.subjectId });
                  successMessage = "Member removed successfully.";
                  clearMessages();
                } catch (e) {
                  errorMessage = messageFrom(e, "Failed to remove member. Please try again.");
                  removingMemberIds = new Set([...removingMemberIds].filter(x => x !== member.subjectId));
                  clearMessages();
                }
              }}
            />
          </div>
        {/each}
      {/if}
      {/if}

      <!-- Create Invite Link (inline card) -->
      {#if canInvite}
        {#if showCreateInvite && tenantId}
          <CreateInviteCard
            roles={allRoles}
            tenantId={tenantId}
            onCreated={() => {
              successMessage = "Invite link created. Share it with the new member.";
              clearMessages();
            }}
            onCancel={() => (showCreateInvite = false)}
          />
        {:else}
          <button
            type="button"
            class="w-full rounded-xl border border-dashed border-muted-foreground/25 hover:border-muted-foreground/50 bg-transparent hover:bg-muted/50 transition-colors py-4 flex items-center justify-center gap-2 text-sm text-muted-foreground hover:text-foreground cursor-pointer"
            onclick={() => (showCreateInvite = true)}
            {@attach coachmark({
              key: "setup-invite.create-link",
              title: "Start here",
              description: "Create a shareable link to invite a caretaker, partner, or clinician.",
            })}
          >
            <Link class="h-4 w-4" />
            Create Invite Link
          </button>
        {/if}
      {/if}

      <!-- Pending Invites -->
      {#if canInvite && activeInvites.length > 0 && !showCreateInvite}
        <PendingInvitesList
          invites={activeInvites}
          roles={allRoles}
          isRevoking={isRevokingInvite !== null}
          onRevoke={async (inviteId) => {
            if (!tenantId) return;
            isRevokingInvite = inviteId;
            errorMessage = null;
            try {
              await revokeInvite({ id: tenantId, inviteId });
              successMessage = "Invite revoked successfully.";
              clearMessages();
            } catch {
              errorMessage = "Failed to revoke invite. Please try again.";
              clearMessages();
            } finally {
              isRevokingInvite = null;
            }
          }}
        />
      {/if}

    </div>
  {/if}

  <!-- Temporary Guest Links (self-gates on sharing.guest) -->
  <GuestLinksSection />

  <!-- Roles & permissions -->
  {#if canManageRoles}
    <RolesSection />
  {/if}

  <!-- Audit Log (lives under Sharing & Privacy — access and change history is a privacy concern) -->
  {#if canViewAudit}
    <a href={resolve("/settings/audit")} class="group block">
      <Card.Root class="transition-colors hover:border-primary/40 hover:bg-muted/40">
        <Card.Content class="flex items-center gap-4 p-4">
          <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
            <ScrollText class="h-5 w-5 text-primary" />
          </div>
          <div class="min-w-0 flex-1">
            <p class="font-medium">Audit Log</p>
            <p class="text-sm text-muted-foreground">
              Review changes and access history.
            </p>
          </div>
          <ChevronRight
            class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
          />
        </Card.Content>
      </Card.Root>
    </a>
  {/if}

  {#if !canInvite && !canManageMembers && !canManageSharing && !canManageRoles && !canCreateGuestLinks && !canViewAudit}
    <Card.Root>
      <Card.Content class="flex flex-col items-center justify-center py-12 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10">
          <ShieldAlert class="h-6 w-6 text-destructive" />
        </div>
        <h2 class="text-lg font-semibold">Access Denied</h2>
        <p class="mt-2 max-w-sm text-sm text-muted-foreground">
          You do not have permission to manage sharing or members. Contact your
          tenant administrator for access.
        </p>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
