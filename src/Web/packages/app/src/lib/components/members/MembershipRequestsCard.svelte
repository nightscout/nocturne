<script lang="ts">
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import UserPlus from "@lucide/svelte/icons/user-plus";
  import Info from "@lucide/svelte/icons/info";
  import {
    getMembershipRequestSettings,
    setMembershipRequestSettings,
    getPendingRequests,
  } from "#lib/api/generated/membershipRequests.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "members.manage"),
  );

  const settingsQuery = $derived(canManage ? getMembershipRequestSettings() : null);
  const pendingQuery = $derived(canManage ? getPendingRequests() : null);
  retainQuery(() => settingsQuery);
  retainQuery(() => pendingQuery);

  let pendingAllow = $state<boolean | null>(null);
  const allow = $derived(pendingAllow ?? settingsQuery?.current?.allowRequests ?? false);
  const pendingCount = $derived(pendingQuery?.current?.length ?? 0);

  let busy = $state(false);
  let errorMessage = $state<string | null>(null);

  async function setAllow(v: boolean) {
    busy = true;
    errorMessage = null;
    pendingAllow = v;
    try {
      await setMembershipRequestSettings({ allowRequests: v });
    } catch (err) {
      errorMessage = describeSubmitError(
        err,
        "Could not update membership requests. Try again."
      );
    } finally {
      busy = false;
      pendingAllow = null;
    }
  }
</script>

{#if canManage}
  <Card.Root>
    <div class="flex items-start gap-4 p-5 @md:p-6">
      <div
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl {allow
          ? 'bg-warning/15 text-warning'
          : 'bg-muted text-muted-foreground'}"
      >
        <UserPlus class="h-5 w-5" />
      </div>
      <div class="min-w-0 flex-1">
        <h2 class="text-lg font-semibold">Membership requests</h2>
        <p class="mt-0.5 max-w-prose text-sm text-muted-foreground">
          Let anyone who reaches your profile ask to become a member. This works
          when public access is on or off. Requests never grant access
          automatically. Each request waits in your queue for approval.
        </p>
        {#if errorMessage}
          <p class="mt-2 text-sm text-destructive">{errorMessage}</p>
        {/if}
      </div>
      <Switch
        checked={allow}
        disabled={busy}
        onCheckedChange={(v: boolean) => setAllow(v)}
        aria-label="Membership requests"
      />
    </div>

    {#if allow}
      <div
        class="flex items-center gap-2 border-t border-border px-5 py-3 text-xs text-muted-foreground @md:px-6"
      >
        <Info class="h-3.5 w-3.5 shrink-0" />
        <span>
          Approved requests join with the role you choose at review.
          <strong class="font-medium text-foreground">
            {pendingCount} pending right now.
          </strong>
        </span>
      </div>
    {/if}
  </Card.Root>
{/if}
