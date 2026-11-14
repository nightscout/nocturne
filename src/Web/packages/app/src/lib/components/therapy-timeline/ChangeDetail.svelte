<script lang="ts">
  /** What one profile change changed, and the glucose of seven days either side of it. */
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { type GlucoseAroundChange, type ProfileChange } from "#lib/api/index.js";
  import { bg, bgLabel, formatMediumDateTime, formatShortDate } from "#lib/utils/formatting.js";
  import { profileActivationSource } from "#lib/utils/profile-activation-label.js";
  import {
    attributeLabel,
    attributeUnit,
    attributeValue,
    isSwitch,
    profileLabel,
    timeOfDay,
  } from "./timeline-format.js";

  interface Props {
    change: ProfileChange;
    /** Undefined while it loads; null when the viewer cannot read glucose. */
    glucose: GlucoseAroundChange | null | undefined;
  }

  let { change, glucose }: Props = $props();

  const before = $derived(
    profileLabel(change.previousProfileName, change.previousPercentage, change.previousTimeshiftHours)
  );
  const after = $derived(profileLabel(change.profileName, change.percentage, change.timeshiftHours));
  const source = $derived(
    change.activation ? (change.activation.enteredBy || profileActivationSource(change.activation)) : undefined
  );

  const sides = $derived(
    glucose
      ? [
          { label: "7 days before", window: glucose.before },
          { label: "7 days after", window: glucose.after },
        ]
      : []
  );
</script>

<div class="space-y-5">
  <div>
    <h3 class="text-base font-semibold leading-tight">
      {isSwitch(change) ? "Profile switch" : "Profile edit"}
    </h3>
    <p class="mt-1 text-sm text-muted-foreground">
      {formatMediumDateTime(new Date(change.mills ?? 0))}
    </p>
    {#if isSwitch(change)}
      <p class="mt-2 flex flex-wrap items-center gap-x-2 text-sm">
        <span>{before}</span>
        <ArrowRight class="size-3.5 text-muted-foreground" aria-label="changed to" />
        <span class="font-medium">{after}</span>
      </p>
    {:else}
      <p class="mt-2 text-sm font-medium">{after}</p>
    {/if}
    {#if source}
      <p class="mt-1 text-xs text-muted-foreground">Source: {source}</p>
    {/if}
    {#if change.activation?.notes}
      <p class="mt-1 text-xs text-muted-foreground">{change.activation.notes}</p>
    {/if}
  </div>

  {#if (change.attributes ?? []).length === 0}
    <p class="text-sm text-muted-foreground">The schedules are the same before and after this change.</p>
  {/if}

  {#each change.attributes ?? [] as attribute (attribute.attribute)}
    <table class="w-full text-sm">
      <caption class="pb-1 text-left text-xs font-medium">
        {attributeLabel(attribute.attribute)}
        {#if attributeUnit(attribute.attribute)}
          <span class="font-normal text-muted-foreground">{attributeUnit(attribute.attribute)}</span>
        {/if}
      </caption>
      <thead>
        <tr class="border-b border-border text-2xs text-muted-foreground">
          <th scope="col" class="py-1 text-left font-normal">From</th>
          <th scope="col" class="py-1 text-right font-normal">Before</th>
          <th scope="col" class="py-1 text-right font-normal">After</th>
        </tr>
      </thead>
      <tbody>
        {#each attribute.rows ?? [] as row (row.timeAsSeconds)}
          <tr class="border-b border-border/60 tabular-nums {row.changed ? '' : 'text-muted-foreground'}">
            <th scope="row" class="py-1 text-left font-normal">{timeOfDay(row.timeAsSeconds)}</th>
            <td class="py-1 text-right">
              {attributeValue(attribute.attribute, row.before, row.beforeHigh)}
            </td>
            <td class="py-1 text-right {row.changed ? 'font-semibold' : ''}">
              {attributeValue(attribute.attribute, row.after, row.afterHigh)}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/each}

  {#if glucose !== null}
    <div>
      <h4 class="text-xs font-medium">Glucose around this change</h4>
      {#if glucose === undefined}
        <p class="mt-2 text-sm text-muted-foreground">Loading…</p>
      {:else}
        <dl class="mt-2 grid grid-cols-2 gap-px overflow-hidden rounded-md border border-border bg-border">
          {#each sides as side (side.label)}
            <div class="bg-card px-3 py-2">
              <dt class="text-2xs text-muted-foreground">
                {side.label}
                <span class="block">
                  {formatShortDate(new Date(side.window?.startMills ?? 0))} – {formatShortDate(
                    new Date(side.window?.endMills ?? 0)
                  )}
                </span>
              </dt>
              {#if side.window?.readings}
                <dd class="m-0 mt-1 text-sm tabular-nums">
                  <span class="font-semibold">{bg(side.window.average ?? 0)}</span>
                  <span class="text-xs text-muted-foreground">{bgLabel()} average</span>
                </dd>
                <dd class="m-0 text-sm tabular-nums">
                  <span class="font-semibold">{(side.window.timeInRange ?? 0).toFixed(0)}%</span>
                  <span class="text-xs text-muted-foreground">time in range</span>
                </dd>
                <dd class="m-0 text-2xs text-muted-foreground tabular-nums">
                  {side.window.readings} readings
                </dd>
              {:else}
                <dd class="m-0 mt-1 text-sm text-muted-foreground">No readings</dd>
              {/if}
            </div>
          {/each}
        </dl>
      {/if}
    </div>
  {/if}
</div>
