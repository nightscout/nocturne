<script lang="ts">
  import { AlertTriangle, Bell, BellOff, Clock, Eye } from "lucide-svelte";
  import PermissionSummary from "$lib/components/rbac/PermissionSummary.svelte";
  import type { JoinInviteInfo } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    invite: JoinInviteInfo;
    signedIn: boolean;
  }

  let { invite, signedIn }: Props = $props();

  const siteName = $derived(invite.tenantName || "this site");
  const roleNames = $derived(invite.roleNames ?? []);
</script>

<div class="space-y-4 text-sm" data-testid="invite-summary">
  <p class="text-base">
    {#if invite.createdByName}
      <strong>{invite.createdByName}</strong>
      has invited you to join
    {:else}
      You've been invited to join
    {/if}
    <strong>{siteName}</strong>
    on Nocturne.
  </p>

  {#if !invite.grantsAccess}
    <div
      class="flex items-start gap-2 rounded-md border border-warning/30 bg-warning/10 p-3"
    >
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-warning" />
      <p>
        This invite no longer gives access to anything. Ask
        {#if invite.createdByName}
          {invite.createdByName}
        {:else}
          the person who sent it
        {/if}
        for a new invite link.
      </p>
    </div>
  {:else}
    <p class="text-muted-foreground">
      Nocturne is a website for viewing diabetes information, such as glucose
      (blood sugar) readings, that someone has chosen to share with you.
    </p>

    <div class="space-y-2 rounded-md border p-3">
      {#if roleNames.length > 0}
        <p>
          Your role: <strong>{roleNames.join(", ")}</strong>
        </p>
      {/if}
      <p class="text-muted-foreground">What you'll have access to:</p>
      <PermissionSummary permissions={invite.permissions ?? []} />
    </div>

    <ul class="space-y-2">
      {#if invite.isViewOnlyForRecordsAndAccess}
        <li class="flex items-start gap-2">
          <Eye class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
          <span>
            You can view this information. You can't change records, treatment
            settings or who has access.
          </span>
        </li>
      {/if}
      {#if invite.limitTo24Hours}
        <li class="flex items-start gap-2">
          <Clock class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
          <span>You'll only see the last 24 hours of data.</span>
        </li>
      {/if}
      <li class="flex items-start gap-2">
        <Bell class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
        <span>
          While you have Nocturne open, you'll see alerts as they happen.
          Getting alerts when Nocturne isn't open, for example on your phone,
          depends on how alerts have been set up. Ask the person who invited
          you.
        </span>
      </li>
      <li class="flex items-start gap-2">
        <BellOff class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
        <span>
          You can press Acknowledge on an alert to mark it as seen. This stops
          the alert being passed on to other people.
        </span>
      </li>
    </ul>

    <p>
      {#if signedIn}
        Next, accept the invite below. You'll then go to the {siteName} dashboard.
      {:else}
        Next, choose how you'd like to sign in below. Once you're in, you'll go
        to the {siteName} dashboard.
      {/if}
    </p>
  {/if}
</div>
