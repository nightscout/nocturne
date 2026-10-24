<script lang="ts">
  import { isoNow } from "$lib/utils/now";
  import { page } from "$app/state";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import {
    getActiveAlerts,
    acknowledgeExcursion,
    markSeen,
  } from "$api/generated/alerts.generated.remote";
  import { Button } from "$lib/components/ui/button";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import Check from "@lucide/svelte/icons/check";
  import { time } from "$lib/utils/formatting";
  import { formatTimeSince } from "./alertTime.svelte";
  import { severity } from "./severity";
  import { severityLabel } from "./severityLabel.svelte";
  import { seenBy } from "./receipts";
  import { conditionLabel } from "./conditionLabel.svelte";
  import { markSeenOnThisDevice, seenHere } from "./seen-here.svelte";

  // Reactive query: reading `.current` subscribes this component, so an
  // optimistic withOverride from any acknowledge (here or the fresh-fire
  // toast) shows immediately and is reconciled by the single-flight refresh.
  const activeAlerts = getActiveAlerts();

  let acknowledgingId = $state<string | null>(null);

  // Without alerts.readwrite the server mutes the alert for this member only,
  // so the button has to say that rather than promise to stop it for everyone.
  const acknowledgesForEveryone = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite")
  );

  // A share or guest link cannot acknowledge or mute; it records that it saw
  // the alarm, which takes it off this device and changes nothing elsewhere.
  const hasAccount = $derived(!!page.data.hasAccount);
  const viewer = $derived({
    hasAccount,
    isShareHost: !!page.data.isShareHost,
  });

  // Acknowledging or muting is the only way off this surface for a member. The
  // X that used to sit here hid a live, unacknowledged alert for the rest of
  // the session while recording nothing server-side and halting no escalation.
  const visibleAlerts = $derived(
    (activeAlerts.current ?? []).filter(
      (a) => !a.acknowledgedAt && !a.mutedByCaller && !seenHere(a, viewer)
    )
  );

  async function handleSeen(id: string) {
    acknowledgingId = id;
    try {
      await markSeen(id);
      markSeenOnThisDevice(id);
    } catch {
      // The alarm closed, or the press was throttled: the next poll shows
      // which, and the banner stays up until it does.
      await activeAlerts.refresh();
    } finally {
      acknowledgingId = null;
    }
  }

  async function handleAcknowledge(id: string) {
    acknowledgingId = id;
    try {
      // Optimistically drop this excursion out of visibleAlerts at once; the
      // single-flight refresh confirms server-side.
      await acknowledgeExcursion({
        excursionId: id,
        // Who acknowledged is taken from the session server-side; sending a
        // fixed "web_user" made this unanswerable on a multi-caregiver tenant.
        request: {},
      }).updates(
        activeAlerts.withOverride((current) =>
          (current ?? []).map((a) =>
            a.id !== id
              ? a
              : acknowledgesForEveryone
                ? { ...a, acknowledgedAt: isoNow() }
                : { ...a, mutedByCaller: true }
          )
        )
      );
    } finally {
      acknowledgingId = null;
    }
  }

</script>

{#if visibleAlerts.length > 0}
  <div class="border-b print:hidden">
    {#each visibleAlerts as alert (alert.id)}
      <!-- Coloured by the rule's own severity: styling every banner as
           destructive made an info rule indistinguishable from a critical low. -->
      <div
        class="container mx-auto flex items-center gap-3 border-b px-4 py-2 max-w-7xl last:border-b-0 {severity(
          alert.severity,
          'strip'
        )}"
      >
        <AlertTriangle class="h-4 w-4 shrink-0" />
        <div class="flex-1 min-w-0">
          <!-- Named as well as coloured: colour alone is unavailable to a
               screen reader and to anyone who can't distinguish these hues. -->
          <span class="text-2xs font-semibold uppercase tracking-wider">
            {severityLabel(alert.severity)}
          </span>
          <span class="text-sm font-medium">
            {alert.ruleName ?? "Alert"}
          </span>
          <span class="text-sm text-muted-foreground mx-2">
            {conditionLabel(alert.conditionType)}
          </span>
          <span class="text-xs text-muted-foreground">
            {formatTimeSince(alert.startedAt)}
          </span>
          {#if alert.snoozedUntil}
            <!-- Still listed: a snooze pauses notifications, it does not
                 acknowledge the alert. -->
            <span
              class="ml-2 inline-flex items-center gap-1 text-xs text-muted-foreground"
            >
              <BellOff class="h-3 w-3" />
              Snoozed until {time(alert.snoozedUntil)}
            </span>
          {/if}
          {#if hasAccount && seenBy(alert.receipts)}
            <span class="ml-2 text-xs text-muted-foreground">
              {seenBy(alert.receipts)}
            </span>
          {/if}
        </div>
        <div class="flex items-center gap-2 shrink-0">
          {#if !hasAccount}
            <Button
              variant="outline"
              size="xs"
              onclick={() => handleSeen(alert.id ?? "")}
              disabled={acknowledgingId === alert.id}
            >
              <Check class="h-3 w-3 mr-1" />
              Seen
            </Button>
          {:else if !alert.acknowledgedAt}
            <Button
              variant="outline"
              size="xs"
              onclick={() => handleAcknowledge(alert.id ?? "")}
              disabled={acknowledgingId === alert.id}
            >
              {#if acknowledgesForEveryone}
                <Check class="h-3 w-3 mr-1" />
                Acknowledge
              {:else}
                <BellOff class="h-3 w-3 mr-1" />
                Mute for me
              {/if}
            </Button>
          {/if}

        </div>
      </div>
    {/each}
  </div>
{/if}
