<script lang="ts">
  import { tick } from "svelte";
  import { page } from "$app/state";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { TrackerCategory, type LegacyConsumableKind, type LegacyFallbackDto } from "$lib/api";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import { hiddenLegacyFallbacks } from "$lib/stores/appearance-store.svelte";
  import { formatLocale } from "$lib/utils/formatting";
  import Clock from "@lucide/svelte/icons/clock";
  import EyeOff from "@lucide/svelte/icons/eye-off";
  import Plus from "@lucide/svelte/icons/plus";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { reachedUrgency } from "$lib/components/trackers/schedule";
  import {
    ageHoursSince,
    formatAge,
    formatLifespan,
  } from "$lib/components/trackers/age.svelte";
  import { legacyKindLabel } from "$lib/components/trackers/legacy-kind-label.svelte";
  import {
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
    trackerPillClasses,
  } from "./tracker-pill-style.svelte";

  interface Props {
    fallback: LegacyFallbackDto;
    /** Epoch milliseconds the age and the reached level are judged against. */
    now: number;
    /** Opens the conversion with this kind selected. */
    onCreateTracker?: (kind: LegacyConsumableKind) => void;
    class?: string;
  }

  let { fallback, now, onCreateTracker, class: className }: Props = $props();

  let popoverOpen = $state(false);
  let trigger = $state<HTMLElement | null>(null);

  // Converting creates a tenant tracker, which the server gates on alerts.readwrite. Hiding is the
  // viewer's own preference and needs nothing more than seeing the pill.
  const canCreate = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "alerts.readwrite")
  );

  const level = $derived(alertLevel(reachedUrgency(fallback, now)));
  const alertPrefix = $derived(alertLevelPrefix(level));

  // eslint-disable-next-line shadcn/require-static-classes -- the tracker pill's shape, and PopoverTrigger renders unstyled.
  const pillClasses = $derived(trackerPillClasses(className));

  const category = $derived(fallback.spec?.category ?? TrackerCategory.Custom);
  const label = $derived(fallback.kind ? legacyKindLabel(fallback.kind) : "");
  const ageDisplay = $derived(
    formatAge(ageHoursSince(fallback.latestEventAt, now) ?? fallback.ageHours)
  );

  function createTracker() {
    if (!fallback.kind) return;
    popoverOpen = false;
    onCreateTracker?.(fallback.kind);
  }

  /** The next pill in the rail, else the previous one, else the rail itself. */
  function focusAfterHide(): HTMLElement | null {
    const rail = trigger?.closest<HTMLElement>("[data-pill-rail]") ?? null;
    const pills = rail ? [...rail.querySelectorAll<HTMLElement>("[data-pill-trigger]")] : [];
    const index = trigger ? pills.indexOf(trigger) : -1;
    return (index >= 0 ? (pills[index + 1] ?? pills[index - 1]) : undefined) ?? rail;
  }

  /** The rail takes focus only from here, so a click on its empty space does not move focus. */
  function focusTarget(target: HTMLElement) {
    if (!target.hasAttribute("data-pill-rail")) return target.focus();
    target.tabIndex = -1;
    target.addEventListener("blur", () => target.removeAttribute("tabindex"), { once: true });
    target.focus();
  }

  async function hide() {
    const kind = fallback.kind;
    if (!kind) return;
    const next = focusAfterHide();
    popoverOpen = false;
    if (!hiddenLegacyFallbacks.current.includes(kind)) {
      hiddenLegacyFallbacks.current = [...hiddenLegacyFallbacks.current, kind];
    }
    await tick();
    if (next) focusTarget(next);
  }
</script>

<Popover.Root bind:open={popoverOpen}>
  <Popover.Trigger
    bind:ref={trigger}
    class={pillClasses}
    data-pill-trigger
    data-testid="legacy-age-pill"
  >
    <span class="flex items-center gap-1 text-xs text-muted-foreground">
      <TrackerCategoryIcon {category} class="size-3" />
      {label}
    </span>
    <span class="flex items-center gap-1 text-sm font-medium tabular-nums {VALUE_TONE[level]}">
      {#if alertPrefix}
        <TriangleAlert class="size-3.5" aria-hidden="true" />
        <span class="sr-only">{alertPrefix}</span>
      {/if}
      {ageDisplay}
    </span>
  </Popover.Trigger>
  <Popover.Content
    class="w-72 p-0"
    align="center"
    side="bottom"
    data-testid="legacy-age-pill-popover"
  >
    <div class="border-b border-border px-4 py-3">
      <h4 class="flex items-center gap-2 text-sm font-semibold">
        <TrackerCategoryIcon {category} class="h-4 w-4" />
        {label}
      </h4>
      <p class="text-xs text-muted-foreground">
        No tracker records this item. The age starts at the last change.
      </p>
    </div>
    <div class="space-y-2 px-4 py-3">
      <div class="flex items-center justify-between text-sm">
        <span class="flex items-center gap-1.5 text-muted-foreground">
          <Clock class="h-3.5 w-3.5" />
          Age
        </span>
        <span class="font-medium">{ageDisplay}</span>
      </div>
      {#if fallback.latestEventAt}
        <div class="flex items-center justify-between text-sm">
          <span class="text-muted-foreground">Last change</span>
          <span class="font-medium">
            {new Date(fallback.latestEventAt).toLocaleString(formatLocale(), {
              month: "short",
              day: "numeric",
              hour: "2-digit",
              minute: "2-digit",
            })}
          </span>
        </div>
      {/if}
      {#if fallback.spec?.lifespanHours}
        <div class="flex items-center justify-between text-sm">
          <span class="text-muted-foreground">Expected lifespan</span>
          <span class="font-medium">{formatLifespan(fallback.spec.lifespanHours)}</span>
        </div>
      {/if}
    </div>
    <div class="flex gap-2 border-t border-border p-2">
      {#if canCreate}
        <Button variant="outline" size="sm" class="flex-1" onclick={createTracker}>
          <Plus class="mr-2 h-4 w-4" />
          Create a tracker
        </Button>
      {/if}
      <Button variant="ghost" size="sm" class={canCreate ? "" : "flex-1"} onclick={hide}>
        <EyeOff class="mr-2 h-4 w-4" />
        Hide
      </Button>
    </div>
  </Popover.Content>
</Popover.Root>
