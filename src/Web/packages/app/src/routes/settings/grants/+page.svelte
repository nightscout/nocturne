<script lang="ts">
  import { enhance } from "$app/forms";
  import { invalidateAll } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as Tabs from "$lib/components/ui/tabs";
  import { Badge } from "$lib/components/ui/badge";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Shield,
    Users,
    Trash2,
    Plus,
    Check,
    AlertTriangle,
    Clock,
  } from "lucide-svelte";
  import { formatDate } from "$lib/utils/formatting";
  import type { Grant } from "./+page.server";

  const { data, form } = $props();

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

  const grants = $derived((data.grants ?? []) as Grant[]);
  const appGrants = $derived(grants.filter((g) => g.grantType === "app"));
  const followerGrants = $derived(grants.filter((g) => g.grantType === "follower"));

  let activeTab = $state("apps");
  let showAddFollower = $state(false);
  let followerEmail = $state("");
  let followerLabel = $state("");
  let selectedScopes = $state<Record<string, boolean>>({
    "entries.read": true,
    "treatments.read": false,
    "devicestatus.read": false,
    "profile.read": false,
    "notifications.read": false,
    "reports.read": false,
  });

  const selectedScopeList = $derived(
    Object.entries(selectedScopes)
      .filter(([, v]) => v)
      .map(([k]) => k)
  );

  const formError = $derived(
    form && "error" in form ? (form.error as string) : null
  );

  const formAction = $derived(
    form && "action" in form ? (form.action as string) : null
  );

  const formSuccess = $derived(
    form && "success" in form && form.success === true
  );

  /** Reset the add-follower form to its defaults. */
  function resetFollowerForm() {
    followerEmail = "";
    followerLabel = "";
    selectedScopes = {
      "entries.read": true,
      "treatments.read": false,
      "devicestatus.read": false,
      "profile.read": false,
      "notifications.read": false,
      "reports.read": false,
    };
    showAddFollower = false;
  }
</script>

<svelte:head>
  <title>Grants - Settings - Nocturne</title>
</svelte:head>

<div class="w-full py-6 space-y-6">
  <div class="space-y-1">
    <h1 class="text-2xl font-bold tracking-tight">Grants</h1>
    <p class="text-muted-foreground">
      Manage applications and followers that have access to your data
    </p>
  </div>

  {#if formError}
    <div
      class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
    >
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{formError}</p>
    </div>
  {/if}

  {#if formSuccess && formAction === "revoke"}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">
        Grant revoked successfully.
      </p>
    </div>
  {/if}

  {#if formSuccess && formAction === "addFollower"}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">
        Follower added successfully.
      </p>
    </div>
  {/if}

  {#if formSuccess && formAction === "updateGrant"}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">
        Grant updated successfully.
      </p>
    </div>
  {/if}

  <Tabs.Root bind:value={activeTab}>
    <Tabs.List class="w-full">
      <Tabs.Trigger value="apps">
        <Shield class="h-4 w-4" />
        Connected Apps
      </Tabs.Trigger>
      <Tabs.Trigger value="followers">
        <Users class="h-4 w-4" />
        Followers
      </Tabs.Trigger>
    </Tabs.List>

    <!-- Connected Apps Tab -->
    <Tabs.Content value="apps" class="space-y-4 pt-4">
      {#if appGrants.length === 0}
        <Card.Root>
          <Card.Content class="flex flex-col items-center justify-center py-12 text-center">
            <div
              class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
            >
              <Shield class="h-6 w-6 text-muted-foreground" />
            </div>
            <p class="text-sm text-muted-foreground max-w-sm">
              No connected applications. When you authorize apps to access your
              data, they will appear here.
            </p>
          </Card.Content>
        </Card.Root>
      {:else}
        {#each appGrants as grant (grant.id)}
          <Card.Root>
            <Card.Header>
              <div class="flex items-start justify-between gap-4">
                <div class="space-y-1 flex-1 min-w-0">
                  <Card.Title class="flex items-center gap-2 flex-wrap">
                    <span class="truncate">
                      {grant.clientDisplayName ?? grant.clientId}
                    </span>
                    {#if grant.isKnownClient}
                      <Badge variant="secondary" class="shrink-0">
                        <Check class="mr-1 h-3 w-3" />
                        Verified
                      </Badge>
                    {/if}
                  </Card.Title>
                  {#if grant.label}
                    <Card.Description>{grant.label}</Card.Description>
                  {/if}
                </div>
                <form
                  method="POST"
                  action="?/revoke"
                  use:enhance={() => {
                    return async ({ update }) => {
                      await update();
                      await invalidateAll();
                    };
                  }}
                >
                  <input type="hidden" name="grant_id" value={grant.id} />
                  <Button type="submit" variant="outline" size="sm" class="text-destructive border-destructive/30 hover:bg-destructive/10 shrink-0">
                    <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                    Revoke
                  </Button>
                </form>
              </div>
            </Card.Header>
            <Card.Content class="space-y-4">
              <div>
                <p class="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  Permissions
                </p>
                <ul class="space-y-1.5">
                  {#each grant.scopes as scope}
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
    </Tabs.Content>

    <!-- Followers Tab -->
    <Tabs.Content value="followers" class="space-y-4 pt-4">
      <div class="flex items-center justify-between">
        <p class="text-sm text-muted-foreground">
          Share your data with caregivers and family members
        </p>
        {#if !showAddFollower}
          <Button
            variant="outline"
            size="sm"
            onclick={() => (showAddFollower = true)}
          >
            <Plus class="mr-1.5 h-3.5 w-3.5" />
            Add Follower
          </Button>
        {/if}
      </div>

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
            <form
              method="POST"
              action="?/addFollower"
              use:enhance={() => {
                return async ({ result, update }) => {
                  await update();
                  if (result.type === "success") {
                    resetFollowerForm();
                    await invalidateAll();
                  }
                };
              }}
              class="space-y-4"
            >
              <div class="space-y-2">
                <Label for="follower-email">Email address</Label>
                <Input
                  id="follower-email"
                  type="email"
                  name="follower_email"
                  placeholder="caregiver@example.com"
                  required
                  bind:value={followerEmail}
                />
              </div>

              <div class="space-y-2">
                <Label for="follower-label">Label (optional)</Label>
                <Input
                  id="follower-label"
                  type="text"
                  name="label"
                  placeholder="e.g. Mom, Endocrinologist"
                  bind:value={followerLabel}
                />
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

              <input type="hidden" name="scopes" value={selectedScopeList.join(",")} />

              {#if formError && formAction === "addFollower"}
                <div
                  class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
                >
                  <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                  <p class="text-sm text-destructive">{formError}</p>
                </div>
              {/if}

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
                  type="submit"
                  class="flex-1"
                  disabled={selectedScopeList.length === 0 || !followerEmail}
                >
                  Add Follower
                </Button>
              </div>
            </form>
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
                <form
                  method="POST"
                  action="?/revoke"
                  use:enhance={() => {
                    return async ({ update }) => {
                      await update();
                      await invalidateAll();
                    };
                  }}
                >
                  <input type="hidden" name="grant_id" value={grant.id} />
                  <Button type="submit" variant="outline" size="sm" class="text-destructive border-destructive/30 hover:bg-destructive/10 shrink-0">
                    <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                    Revoke
                  </Button>
                </form>
              </div>
            </Card.Header>
            <Card.Content class="space-y-4">
              <div>
                <p class="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  Shared Data
                </p>
                <ul class="space-y-1.5">
                  {#each grant.scopes as scope}
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
    </Tabs.Content>
  </Tabs.Root>
</div>
