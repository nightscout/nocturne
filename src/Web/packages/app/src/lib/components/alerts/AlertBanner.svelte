<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import Check from "@lucide/svelte/icons/check";
  import { formatDateTimeCompact, relativeAge, time, toDate } from "#lib/utils/formatting.js";
  import { severity } from "./severity";
  import { severityLabel } from "./severityLabel.svelte";
  import { seenBy } from "./receipts";
  import { conditionLabel } from "./conditionLabel.svelte";
  import { createAlertActions, SNOOZE_MINUTES } from "./alert-actions.svelte";

  // The one surface that lists open alerts. A row leaves when the server closes
  // the excursion, or when the caller acknowledges, mutes or marks it seen. It
  // has no close button: that hid a live alert while recording nothing
  // server-side and halting no escalation.
  const actions = createAlertActions();
  const visibleAlerts = $derived(actions.alerts.filter((a) => actions.isOutstanding(a)));
</script>

{#if visibleAlerts.length > 0}
  <div class="@container border-b print:hidden" role="region" aria-label="Active alerts">
    {#each visibleAlerts as alert (alert.id)}
      <!-- Coloured by the rule's own severity: styling every banner as
           destructive made an info rule indistinguishable from a critical low. -->
      <div
        class="container mx-auto flex flex-col gap-2 border-b px-4 py-2 max-w-7xl last:border-b-0 @xl:flex-row @xl:items-center @xl:gap-3 {severity(
          alert.severity,
          'strip'
        )}"
      >
        <div class="flex min-w-0 items-start gap-2 @xl:flex-1 @xl:items-center">
          <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 @xl:mt-0" />
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
            <span
              class="text-xs text-muted-foreground"
              title={toDate(alert.startedAt) ? formatDateTimeCompact(alert.startedAt) : undefined}
            >
              {relativeAge(alert.startedAt, { fallback: "Unknown" })}
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
            {#if actions.hasAccount && seenBy(alert.receipts)}
              <span class="ml-2 text-xs text-muted-foreground">
                {seenBy(alert.receipts)}
              </span>
            {/if}
          </div>
        </div>
        <div class="ml-6 flex flex-wrap items-center gap-1 shrink-0 @xl:ml-0">
          {#if !alert.snoozedUntil && actions.snoozableInstance(alert)}
            <span class="text-xs text-muted-foreground">Snooze</span>
            {#each SNOOZE_MINUTES as minutes (minutes)}
              <Button
                variant="ghost"
                size="xs"
                onclick={() => actions.snooze(alert, minutes)}
                disabled={actions.busyId === alert.id}
                aria-label="Snooze for {minutes} minutes"
              >
                {minutes}m
              </Button>
            {/each}
          {/if}
          {#if !actions.hasAccount}
            <Button
              variant="outline"
              size="xs"
              onclick={() => actions.seen(alert.id ?? "")}
              disabled={actions.busyId === alert.id}
            >
              <Check class="h-3 w-3 mr-1" />
              Seen
            </Button>
          {:else}
            <Button
              variant="outline"
              size="xs"
              onclick={() => actions.acknowledge(alert.id ?? "")}
              disabled={actions.busyId === alert.id}
            >
              {#if actions.acknowledgesForEveryone}
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
