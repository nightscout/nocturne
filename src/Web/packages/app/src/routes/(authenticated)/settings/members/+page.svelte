<script lang="ts">
  import { page } from "$app/state";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import {
    Users,
    Check,
    AlertTriangle,
    Link,
    UserPlus,
    ShieldAlert,
  } from "lucide-svelte";
  import { getCurrentTenantId } from "../current-tenant.remote";
  import { getMembers } from "$lib/api/generated/memberInvites.generated.remote";
  import {
    listInvites,
    revokeInvite,
    removeMember,
  } from "$api/generated/tenants.generated.remote";
  import { getRoles } from "$lib/api/generated/roles.generated.remote";
  import {
    setMemberRoles,
    setMemberPermissions,
  } from "$lib/api/generated/memberInvites.generated.remote";
  import { coachmark } from "@nocturne/coach";
  import CreateInviteCard from "$lib/components/members/CreateInviteCard.svelte";
  import PendingInvitesList from "$lib/components/members/PendingInvitesList.svelte";
  import MemberCard from "$lib/components/members/MemberCard.svelte";
  import GuestLinksSection from "$lib/components/members/GuestLinksSection.svelte";

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

  // Tenant
  const tenantIdQuery = $derived(getCurrentTenantId());
  const tenantId = $derived(tenantIdQuery.current ?? undefined);

  // Queries
  const membersQuery = $derived(getMembers());
  const invitesQuery = $derived(tenantId ? listInvites(tenantId) : null);
  const rolesQuery = $derived(getRoles());

  // Data
  const allMembers = $derived(membersQuery.current ?? []);
  const invites = $derived(invitesQuery?.current ?? []);
  const activeInvites = $derived(invites.filter((i) => i.isValid));
  const allRoles = $derived(rolesQuery.current ?? []);

  const publicMember = $derived(allMembers.find((m) => m.name === "Public"));
  const sharingConfigured = $derived(
    (publicMember?.roles ?? []).length > 0 ||
      (publicMember?.directPermissions ?? []).length > 0,
  );

  // --- UI state ---
  let showCreateInvite = $state(false);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  // --- Member edit state ---
  let expandedMember = $state<string | null>(null);
  let isSavingMember = $state(false);
  let isRevokingInvite = $state<string | null>(null);

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
    } catch {
      errorMessage = "Failed to update member. Please try again.";
      clearMessages();
    } finally {
      isSavingMember = false;
    }
  }
</script>

<svelte:head>
  <title>Members - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6" {@attach coachmark({ key: "onboarding.sharing", title: "Share with a caretaker", description: "Create an invite link to give a parent, partner, or clinician read-only access to your glucose data.", completedWhen: () => sharingConfigured })}>
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Users class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Members</h1>
      <p class="text-muted-foreground">
        Manage members, invites, and access to your data
      </p>
    </div>
  </div>

  {#if errorMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
    >
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check
        class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
      />
      <p class="text-sm text-green-800 dark:text-green-200">
        {successMessage}
      </p>
    </div>
  {/if}

  <!-- Invite Section -->
  {#if canInvite}
    <div class="space-y-4">
      <div class="flex items-center justify-between gap-4">
        <h2 class="text-lg font-semibold flex items-center gap-2">
          <UserPlus class="h-5 w-5" />
          Invite Members
        </h2>
        {#if !showCreateInvite}
          <Button
            variant="outline"
            size="sm"
            onclick={() => (showCreateInvite = true)}
            {@attach coachmark({
              key: "setup-invite.create-link",
              title: "Start here",
              description: "Create a shareable link to invite a caretaker, partner, or clinician.",
            })}
          >
            <Link class="mr-1.5 h-3.5 w-3.5" />
            Create Invite Link
          </Button>
        {/if}
      </div>

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
      {/if}
    </div>
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

  <!-- Active Members -->
  {#if canManageMembers}
    <div class="space-y-4">
      <h2 class="text-lg font-semibold flex items-center gap-2">
        <Users class="h-5 w-5" />
        Active Members
      </h2>

      {#if allMembers.length === 0}
        <Card.Root>
          <Card.Content
            class="flex flex-col items-center justify-center py-12 text-center"
          >
            <div
              class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
            >
              <Users class="h-6 w-6 text-muted-foreground" />
            </div>
            <p class="text-sm text-muted-foreground max-w-sm">
              No members. Invite someone to share your data.
            </p>
          </Card.Content>
        </Card.Root>
      {:else}
        {#each allMembers as member (member.subjectId)}
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
              saveMemberChanges(member.subjectId!, roleIds, permissions)}
            onRemove={async () => {
              if (!tenantId || !member.subjectId) return;
              errorMessage = null;
              try {
                await removeMember({ id: tenantId, subjectId: member.subjectId });
                successMessage = "Member removed successfully.";
                clearMessages();
              } catch {
                errorMessage = "Failed to remove member. Please try again.";
                clearMessages();
              }
            }}
          />
        {/each}
      {/if}
    </div>
  {/if}

  <!-- Temporary Guest Links -->
  <GuestLinksSection />

  {#if !canInvite && !canManageMembers}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
        >
          <ShieldAlert class="h-6 w-6 text-destructive" />
        </div>
        <h2 class="text-lg font-semibold">Access Denied</h2>
        <p class="text-sm text-muted-foreground max-w-sm mt-2">
          You do not have permission to manage members. Contact your tenant
          administrator for access.
        </p>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
