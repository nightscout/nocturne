<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import Check from "@lucide/svelte/icons/check";
  import { playAlarmSound, stopAlarmSound } from "#lib/audio/alarm-sounds.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { formatDateTimeCompact, relativeAge, toDate } from "#lib/utils/formatting.js";
  import { createAlertActions, SNOOZE_MINUTES } from "./alert-actions.svelte";

  /**
   * Covers the app while a critical alert is outstanding and not snoozed. Only an action
   * that the server records clears it: acknowledge, mute, snooze, or Seen on a link
   * without an account. It also goes when the server closes the excursion. Warning and
   * info alerts stay in the banner.
   */
  const actions = createAlertActions();
  const clock = new Now();

  const critical = $derived(
    actions.alerts.filter(
      (a) => a.severity === "critical" && !a.snoozedUntil && actions.isOutstanding(a)
    )
  );
  const current = $derived(critical[0]);
  const currentId = $derived(current?.id);

  let primaryAction = $state<HTMLElement | null>(null);

  // Keyed on the id, so the sound starts once for each new critical alert and not
  // on every poll. The browser can block audio until the page has had a click;
  // the screen still shows.
  $effect(() => {
    if (!currentId) return;
    void playAlarmSound("alarm-urgent", { minDurationSeconds: 60 }).catch((err: unknown) => {
      console.warn("Critical alert sound did not play", err);
    });
    return () => stopAlarmSound();
  });

  $effect(() => {
    if (currentId) primaryAction?.focus();
  });

  // A modal dialog sits in the browser's top layer, above every z-index (the coach-mark
  // tour draws at 9999), and makes the page behind it inert. Escape must not close it;
  // Chrome closes it anyway on a second Escape with no click between, so it reopens.
  function modal(dialog: HTMLDialogElement) {
    const keepOpen = () => {
      if (!dialog.open) dialog.showModal();
    };
    keepOpen();
    dialog.addEventListener("close", keepOpen);
    return () => {
      dialog.removeEventListener("close", keepOpen);
      dialog.close();
    };
  }
</script>

{#if current}
  <dialog
    {@attach modal}
    oncancel={(e) => e.preventDefault()}
    role="alertdialog"
    aria-labelledby="critical-alert-title"
    aria-describedby="critical-alert-time"
    class="m-0 hidden open:flex h-dvh max-h-none w-screen max-w-none flex-col items-center justify-center gap-8 border-0 bg-status-critical p-6 text-white print:hidden"
  >
    <div class="flex flex-col items-center gap-3 text-center">
      <CircleAlert class="h-16 w-16" />
      <span class="text-sm font-semibold uppercase tracking-widest">Critical alert</span>
      <h2 id="critical-alert-title" class="text-4xl font-black tracking-tight sm:text-6xl">
        {current.ruleName || "Alert"}
      </h2>
      <span
        id="critical-alert-time"
        class="text-lg opacity-90"
        title={toDate(current.startedAt) ? formatDateTimeCompact(current.startedAt) : undefined}
      >
        {relativeAge(current.startedAt, { now: clock.current, fallback: "Unknown" })}
      </span>
      {#if critical.length > 1}
        <span class="text-sm opacity-90">
          {critical.length - 1} more critical {critical.length === 2 ? "alert" : "alerts"}
        </span>
      {/if}
    </div>

    <div class="flex w-full max-w-sm flex-col gap-3">
      {#if !actions.hasAccount}
        <Button
          bind:ref={primaryAction}
          size="xl"
          variant="secondary"
          onclick={() => actions.seen(current.id ?? "")}
          disabled={actions.busyId === current.id}
        >
          <Check />
          Seen
        </Button>
      {:else}
        <Button
          bind:ref={primaryAction}
          size="xl"
          variant="secondary"
          onclick={() => actions.acknowledge(current.id ?? "")}
          disabled={actions.busyId === current.id}
        >
          {#if actions.acknowledgesForEveryone}
            <Check />
            Acknowledge
          {:else}
            <BellOff />
            Mute for me
          {/if}
        </Button>
      {/if}

      {#if actions.snoozableInstance(current)}
        <span class="mt-2 text-center text-sm font-semibold uppercase tracking-widest opacity-90">
          Snooze
        </span>
        <div class="grid grid-cols-4 gap-2" role="group" aria-label="Snooze">
          {#each SNOOZE_MINUTES as minutes (minutes)}
            <Button
              variant="overlay"
              size="lg"
              onclick={() => actions.snooze(current, minutes)}
              disabled={actions.busyId === current.id}
              aria-label="Snooze for {minutes} minutes"
            >
              {minutes}m
            </Button>
          {/each}
        </div>
      {/if}
    </div>
  </dialog>
{/if}
