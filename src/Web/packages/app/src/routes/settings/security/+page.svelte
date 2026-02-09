<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Users,
    Trash2,
    Plus,
    Check,
    AlertTriangle,
    Clock,
    Link,
    Copy,
    Loader2,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import {
    getGrants,
    getInvites,
    revokeGrant,
    addFollower,
    createInvite,
    revokeInvite,
  } from "$lib/data/oauth.remote";

  /** Human-readable descriptions for each OAuth scope. */
  const scopeDescriptions: Record<string, string> = {
    "entries.read": "View glucose readings",
    "entries.readwrite": "View and record glucose readings",
    "treatments.read": "View treatments",
    "treatments.readwrite": "View and record treatments",
    "devicestatus.read": "View device status",
    "devicestatus.readwrite": "View and update device status",
    "profile.read": "View profile settings",
    "profile.readwrite": "View and update profile settings",
    "notifications.read": "View notifications",
    "notifications.readwrite": "Manage notifications",
    "reports.read": "View reports and analytics",
    "identity.read": "View basic account info",
    "sharing.readwrite": "Manage sharing settings",
    "health.read": "View all health data (read-only)",
    "*": "Full access including delete",
  };

  /** Available scopes for follower grants. */
  const followerScopes = [
    "entries.read",
    "treatments.read",
    "devicestatus.read",
    "profile.read",
    "notifications.read",
    "reports.read",
  ] as const;

  // Remote queries
  const grantsQuery = $derived(getGrants());
  const invitesQuery = $derived(getInvites());

  // Derived data from queries
  const grants = $derived(grantsQuery.current ?? []);
  const invites = $derived(invitesQuery.current ?? []);
  const activeInvites = $derived(invites.filter((i) => i.isValid));
  const followerGrants = $derived(grants.filter((g) => g.grantType === "follower"));

  // UI state
  let showAddFollower = $state(false);
  let showCreateInvite = $state(false);
  let followerEmail = $state("");
  let followerLabel = $state("");
  let followerDisplayName = $state("");
  let createNewAccount = $state(false);
  let temporaryPassword = $state("");
  let inviteLabel = $state("");
  let selectedScopes = $state<Record<string, boolean>>({
    "entries.read": true,
    "treatments.read": false,
    "devicestatus.read": false,
    "profile.read": false,
    "notifications.read": false,
    "reports.read": false,
  });
  let inviteScopes = $state<Record<string, boolean>>({
    "entries.read": true,
    "treatments.read": false,
    "devicestatus.read": false,
    "profile.read": false,
    "notifications.read": false,
    "reports.read": false,
  });
  let allowMultipleUses = $state(false);
  let createdInviteUrl = $state<string | null>(null);
  let copiedInvite = $state(false);

  // Loading/error states
  let isRevoking = $state<string | null>(null);
  let isAddingFollower = $state(false);
  let isCreatingInvite = $state(false);
  let isRevokingInvite = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  const selectedScopeList = $derived(
    Object.entries(selectedScopes)
      .filter(([, v]) => v)
      .map(([k]) => k)
  );

  const inviteScopeList = $derived(
    Object.entries(inviteScopes)
      .filter(([, v]) => v)
      .map(([k]) => k)
  );

  /** Reset the add-follower form to its defaults. */
  function resetFollowerForm() {
    followerEmail = "";
    followerLabel = "";
    followerDisplayName = "";
    createNewAccount = false;
    temporaryPassword = "";
    selectedScopes = {
      "entries.read": true,
      "treatments.read": false,
      "devicestatus.read": false,
      "profile.read": false,
      "notifications.read": false,
      "reports.read": false,
    };
    showAddFollower = false;
    errorMessage = null;
  }

  /** Reset the create-invite form to its defaults. */
  function resetInviteForm() {
    inviteLabel = "";
    inviteScopes = {
      "entries.read": true,
      "treatments.read": false,
      "devicestatus.read": false,
      "profile.read": false,
      "notifications.read": false,
      "reports.read": false,
    };
    allowMultipleUses = false;
    showCreateInvite = false;
    createdInviteUrl = null;
    errorMessage = null;
  }

  /** Copy invite URL to clipboard */
  async function copyInviteUrl() {
    if (createdInviteUrl) {
      await navigator.clipboard.writeText(createdInviteUrl);
      copiedInvite = true;
      setTimeout(() => (copiedInvite = false), 2000);
    }
  }

  /** Clear messages after a delay */
  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  /** Handle revoking a grant */
  async function handleRevokeGrant(grantId: string) {
    isRevoking = grantId;
    errorMessage = null;
    try {
      await revokeGrant({ grantId });
      successMessage = "Grant revoked successfully.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to revoke grant. Please try again.";
      clearMessages();
    } finally {
      isRevoking = null;
    }
  }

  /** Handle adding a follower */
  async function handleAddFollower() {
    isAddingFollower = true;
    errorMessage = null;
    try {
      await addFollower({
        followerEmail,
        scopes: selectedScopeList,
        label: followerLabel || undefined,
        temporaryPassword: createNewAccount ? temporaryPassword : undefined,
        followerDisplayName: createNewAccount ? followerDisplayName : undefined,
      });
      successMessage = "Follower added successfully.";
      resetFollowerForm();
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to add follower. Please try again.";
    } finally {
      isAddingFollower = false;
    }
  }

  /** Handle creating an invite */
  async function handleCreateInvite() {
    isCreatingInvite = true;
    errorMessage = null;
    try {
      const result = await createInvite({
        scopes: inviteScopeList,
        label: inviteLabel || undefined,
        expiresInDays: 7,
        maxUses: allowMultipleUses ? undefined : 1,
      });
      if (result.inviteUrl) {
        createdInviteUrl = result.inviteUrl;
      }
    } catch (err) {
      errorMessage = "Failed to create invite. Please try again.";
    } finally {
      isCreatingInvite = false;
    }
  }

  /** Handle revoking an invite */
  async function handleRevokeInvite(inviteId: string) {
    isRevokingInvite = inviteId;
    errorMessage = null;
    try {
      await revokeInvite({ inviteId });
      successMessage = "Invite revoked successfully.";
      clearMessages();
    } catch (err) {
      errorMessage = "Failed to revoke invite. Please try again.";
      clearMessages();
    } finally {
      isRevokingInvite = null;
    }
  }
</script>

<svelte:head>
  <title>Followers & Sharing - Settings - Nocturne</title>
</svelte:head>

<div class="w-full py-6 space-y-6">
  <div class="space-y-1">
    <h1 class="text-2xl font-bold tracking-tight">Followers & Sharing</h1>
    <p class="text-muted-foreground">
      Share your data with caregivers and family members
    </p>
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
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">
        {successMessage}
      </p>
    </div>
  {/if}

  <div class="space-y-4">
      <div class="flex items-center justify-between gap-4">
        <p class="text-sm text-muted-foreground">
          Share your data with caregivers and family members
        </p>
        <div class="flex gap-2">
          {#if !showCreateInvite && !showAddFollower}
            <Button
              variant="outline"
              size="sm"
              onclick={() => (showCreateInvite = true)}
            >
              <Link class="mr-1.5 h-3.5 w-3.5" />
              Create Invite Link
            </Button>
            <Button
              variant="outline"
              size="sm"
              onclick={() => (showAddFollower = true)}
            >
              <Plus class="mr-1.5 h-3.5 w-3.5" />
              Add by Email
            </Button>
          {/if}
        </div>
      </div>

      <!-- Create Invite Link Card -->
      {#if showCreateInvite}
        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">Create Invite Link</Card.Title>
            <Card.Description>
              Generate a shareable link. Anyone with this link can accept the invite
              after signing in.
            </Card.Description>
          </Card.Header>
          <Card.Content>
            {#if createdInviteUrl}
              <!-- Show the created invite URL -->
              <div class="space-y-4">
                <div class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20">
                  <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
                  <p class="text-sm text-green-800 dark:text-green-200">
                    Invite link created! Share it with your friend or family member.
                  </p>
                </div>

                <div class="flex gap-2">
                  <Input
                    type="text"
                    value={createdInviteUrl}
                    readonly
                    class="font-mono text-sm"
                  />
                  <Button
                    variant="outline"
                    size="icon"
                    onclick={copyInviteUrl}
                  >
                    {#if copiedInvite}
                      <Check class="h-4 w-4 text-green-600" />
                    {:else}
                      <Copy class="h-4 w-4" />
                    {/if}
                  </Button>
                </div>

                <Button
                  variant="outline"
                  class="w-full"
                  onclick={() => resetInviteForm()}
                >
                  Done
                </Button>
              </div>
            {:else}
              <!-- Show the create invite form -->
              <div class="space-y-4">
                <div class="space-y-2">
                  <Label for="invite-label">Label (optional)</Label>
                  <Input
                    id="invite-label"
                    type="text"
                    placeholder="e.g. Mom, Endocrinologist"
                    bind:value={inviteLabel}
                  />
                </div>

                <div class="space-y-3">
                  <Label>Data to share</Label>
                  <div class="grid gap-3 sm:grid-cols-2">
                    {#each followerScopes as scope}
                      <div class="flex items-center gap-2">
                        <Checkbox
                          id="invite-scope-{scope}"
                          checked={inviteScopes[scope]}
                          onCheckedChange={(checked) => {
                            inviteScopes[scope] = checked === true;
                          }}
                        />
                        <label
                          for="invite-scope-{scope}"
                          class="text-sm text-foreground cursor-pointer select-none"
                        >
                          {scopeDescriptions[scope] ?? scope}
                        </label>
                      </div>
                    {/each}
                  </div>
                </div>

                <div class="flex items-start gap-2 rounded-md border p-3 bg-muted/30">
                  <Checkbox
                    id="allow-multiple-uses"
                    checked={allowMultipleUses}
                    onCheckedChange={(checked) => {
                      allowMultipleUses = checked === true;
                    }}
                  />
                  <div class="flex-1">
                    <label
                      for="allow-multiple-uses"
                      class="text-sm font-medium cursor-pointer select-none"
                    >
                      Allow multiple uses
                    </label>
                    <p class="text-xs text-muted-foreground mt-0.5">
                      By default, invite links can only be used once. Enable this to allow unlimited uses.
                    </p>
                  </div>
                </div>

                <div class="flex gap-3">
                  <Button
                    type="button"
                    variant="outline"
                    class="flex-1"
                    onclick={() => resetInviteForm()}
                  >
                    Cancel
                  </Button>
                  <Button
                    type="button"
                    class="flex-1"
                    disabled={inviteScopeList.length === 0 || isCreatingInvite}
                    onclick={handleCreateInvite}
                  >
                    {#if isCreatingInvite}
                      <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
                    {/if}
                    Create Link
                  </Button>
                </div>
              </div>
            {/if}
          </Card.Content>
        </Card.Root>
      {/if}

      <!-- Pending Invites -->
      {#if activeInvites.length > 0 && !showCreateInvite && !showAddFollower}
        <Card.Root>
          <Card.Header class="pb-3">
            <Card.Title class="text-base flex items-center gap-2">
              <Link class="h-4 w-4" />
              Pending Invites
            </Card.Title>
          </Card.Header>
          <Card.Content class="space-y-3">
            {#each activeInvites as invite (invite.id)}
              <div class="flex items-center justify-between gap-4 rounded-md border p-3">
                <div class="space-y-1 flex-1 min-w-0">
                  <p class="text-sm font-medium">
                    {invite.label ?? "Invite Link"}
                  </p>
                  <p class="text-xs text-muted-foreground">
                    Expires {formatDate(invite.expiresAt)}
                    {#if invite.maxUses}
                      &middot; {invite.useCount}/{invite.maxUses} uses
                    {:else}
                      &middot; {invite.useCount} {invite.useCount === 1 ? 'use' : 'uses'}
                    {/if}
                  </p>
                  {#if invite.usedBy && invite.usedBy.length > 0}
                    <div class="mt-2 pt-2 border-t space-y-1">
                      <p class="text-xs font-medium text-muted-foreground uppercase tracking-wider">
                        Used by
                      </p>
                      {#each invite.usedBy as usage}
                        <p class="text-xs text-foreground">
                          <Check class="inline h-3 w-3 mr-1 text-primary" />
                          {usage.followerName ?? usage.followerEmail ?? "Unknown"}
                          <span class="text-muted-foreground ml-1">
                            on {formatDate(usage.usedAt)}
                          </span>
                        </p>
                      {/each}
                    </div>
                  {/if}
                </div>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  class="text-destructive hover:text-destructive shrink-0"
                  disabled={isRevokingInvite === invite.id}
                  onclick={() => handleRevokeInvite(invite.id!)}
                >
                  {#if isRevokingInvite === invite.id}
                    <Loader2 class="h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <Trash2 class="h-3.5 w-3.5" />
                  {/if}
                </Button>
              </div>
            {/each}
          </Card.Content>
        </Card.Root>
      {/if}

      {#if showAddFollower}
        <Card.Root>
          <Card.Header>
            <Card.Title class="text-lg">Add a Follower</Card.Title>
            <Card.Description>
              Grant someone read access to your data by entering their email
              and selecting which data to share.
            </Card.Description>
          </Card.Header>
          <Card.Content>
            <div class="space-y-4">
              <div class="space-y-2">
                <Label for="follower-email">Email address</Label>
                <Input
                  id="follower-email"
                  type="email"
                  placeholder="caregiver@example.com"
                  bind:value={followerEmail}
                />
              </div>

              <div class="space-y-2">
                <Label for="follower-label">Label (optional)</Label>
                <Input
                  id="follower-label"
                  type="text"
                  placeholder="e.g. Mom, Endocrinologist"
                  bind:value={followerLabel}
                />
              </div>

              <div class="space-y-3 rounded-md border p-4">
                <div class="flex items-center gap-2">
                  <Checkbox
                    id="create-new-account"
                    checked={createNewAccount}
                    onCheckedChange={(checked) => {
                      createNewAccount = checked === true;
                      if (!checked) {
                        temporaryPassword = "";
                        followerDisplayName = "";
                      }
                    }}
                  />
                  <label
                    for="create-new-account"
                    class="text-sm font-medium cursor-pointer select-none"
                  >
                    Create new account with temporary password
                  </label>
                </div>

                {#if createNewAccount}
                  <div class="space-y-3 pl-6">
                    <div class="space-y-2">
                      <Label for="follower-display-name">Display name (optional)</Label>
                      <Input
                        id="follower-display-name"
                        type="text"
                        placeholder="e.g. Mom"
                        bind:value={followerDisplayName}
                      />
                    </div>
                    <div class="space-y-2">
                      <Label for="temporary-password">Temporary password</Label>
                      <Input
                        id="temporary-password"
                        type="password"
                        placeholder="Enter a temporary password"
                        bind:value={temporaryPassword}
                      />
                      <p class="text-xs text-muted-foreground">
                        The follower will be required to change this password on first login.
                      </p>
                    </div>
                  </div>
                {/if}
              </div>

              <div class="space-y-3">
                <Label>Data to share</Label>
                <div class="grid gap-3 sm:grid-cols-2">
                  {#each followerScopes as scope}
                    <div class="flex items-center gap-2">
                      <Checkbox
                        id="scope-{scope}"
                        checked={selectedScopes[scope]}
                        onCheckedChange={(checked) => {
                          selectedScopes[scope] = checked === true;
                        }}
                      />
                      <label
                        for="scope-{scope}"
                        class="text-sm text-foreground cursor-pointer select-none"
                      >
                        {scopeDescriptions[scope] ?? scope}
                      </label>
                    </div>
                  {/each}
                </div>
              </div>

              <div class="flex gap-3">
                <Button
                  type="button"
                  variant="outline"
                  class="flex-1"
                  onclick={() => resetFollowerForm()}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  class="flex-1"
                  disabled={selectedScopeList.length === 0 || !followerEmail || isAddingFollower || (createNewAccount && !temporaryPassword)}
                  onclick={handleAddFollower}
                >
                  {#if isAddingFollower}
                    <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
                  {/if}
                  Add Follower
                </Button>
              </div>
            </div>
          </Card.Content>
        </Card.Root>
      {/if}

      {#if followerGrants.length === 0 && !showAddFollower}
        <Card.Root>
          <Card.Content class="flex flex-col items-center justify-center py-12 text-center">
            <div
              class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
            >
              <Users class="h-6 w-6 text-muted-foreground" />
            </div>
            <p class="text-sm text-muted-foreground max-w-sm">
              No followers. Share your data with caregivers by adding a follower.
            </p>
          </Card.Content>
        </Card.Root>
      {:else}
        {#each followerGrants as grant (grant.id)}
          <Card.Root>
            <Card.Header>
              <div class="flex items-start justify-between gap-4">
                <div class="space-y-1 flex-1 min-w-0">
                  <Card.Title class="flex items-center gap-2 flex-wrap">
                    <span class="truncate">
                      {grant.followerName ?? grant.followerEmail ?? "Unknown"}
                    </span>
                  </Card.Title>
                  <Card.Description>
                    {#if grant.followerEmail}
                      {grant.followerEmail}
                    {/if}
                    {#if grant.label}
                      {#if grant.followerEmail}
                        <span class="mx-1">&middot;</span>
                      {/if}
                      {grant.label}
                    {/if}
                  </Card.Description>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  class="text-destructive border-destructive/30 hover:bg-destructive/10 shrink-0"
                  disabled={isRevoking === grant.id}
                  onclick={() => handleRevokeGrant(grant.id!)}
                >
                  {#if isRevoking === grant.id}
                    <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                  {/if}
                  Revoke
                </Button>
              </div>
            </Card.Header>
            <Card.Content class="space-y-4">
              <div>
                <p class="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  Shared Data
                </p>
                <ul class="space-y-1.5">
                  {#each grant.scopes ?? [] as scope}
                    <li class="flex items-start gap-2 text-sm">
                      <Check class="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                      <span class="text-muted-foreground">
                        {scopeDescriptions[scope] ?? scope}
                      </span>
                    </li>
                  {/each}
                </ul>
              </div>

              <Separator />

              <div class="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted-foreground">
                <span class="flex items-center gap-1.5">
                  <Clock class="h-3 w-3" />
                  Created {formatDate(grant.createdAt)}
                </span>
                {#if grant.lastUsedAt}
                  <span class="flex items-center gap-1.5">
                    <Clock class="h-3 w-3" />
                    Last used {formatDate(grant.lastUsedAt)}
                  </span>
                {/if}
              </div>
            </Card.Content>
          </Card.Root>
        {/each}
      {/if}
  </div>
</div>
