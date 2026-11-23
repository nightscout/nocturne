<script lang="ts">
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import {
    accessEndDate,
    earliestAccessEndDay,
    hasOfferedAccessEndDay,
    latestAccessEndDay,
  } from "./access-end.js";

  interface Props {
    id: string;
    /** The last day the person keeps access, as `YYYY-MM-DD`; empty for no end. */
    value: string;
    /** Unix milliseconds from a clock that moves, so the earliest day follows midnight. */
    now: number;
    /** The role chosen is the owner role, whose access does not end. */
    isOwner?: boolean;
    /** When the caller's own access ends. A day cannot pass it. */
    ceiling?: string;
    /** The field edits an existing member, not a new membership. */
    forMember?: boolean;
    /** The member has an end now. */
    hasStoredEnd?: boolean;
    disabled?: boolean;
    /** A day is required, as for access that must end. */
    required?: boolean;
    /** Names the field, in place of the default label. */
    label?: string;
    /** Why the days are limited, in place of the sentence about the caller's own access. */
    limitNote?: string;
  }

  let {
    id,
    value = $bindable(""),
    now,
    isOwner = false,
    ceiling,
    forMember = false,
    hasStoredEnd = false,
    disabled = false,
    required = false,
    label,
    limitNote,
  }: Props = $props();

  const min = $derived(earliestAccessEndDay(now));
  const max = $derived(latestAccessEndDay(ceiling));
  const noneOffered = $derived(!hasOfferedAccessEndDay({ now, ceiling }));
  const mustKeep = $derived(!!ceiling && forMember && hasStoredEnd);
  const ceilingDate = $derived(ceiling ? formatMediumDate(accessEndDate(ceiling)) : "");
  const helpId = $derived(`${id}-help`);

  $effect(() => {
    if (isOwner) value = "";
  });
</script>

{#if isOwner}
  <p class="text-xs text-muted-foreground">An owner's access does not end.</p>
{:else}
  <div class="space-y-2">
    <Label for={id}>
      {label ?? (mustKeep || required ? "Access ends" : "Access ends (optional)")}
    </Label>
    <Input
      {id}
      type="date"
      bind:value
      {min}
      {max}
      disabled={disabled || noneOffered}
      aria-describedby={helpId}
    />
    {#if limitNote}
      <p id={helpId} class="text-xs text-muted-foreground">{limitNote}</p>
    {:else if ceiling && mustKeep}
      <p id={helpId} class="text-xs text-muted-foreground">
        You cannot remove the end date. Your own access ends on {ceilingDate}.
      </p>
    {:else if ceiling && forMember}
      <p id={helpId} class="text-xs text-muted-foreground">
        Your own access ends on {ceilingDate}. You cannot give access that lasts longer.
      </p>
    {:else if ceiling}
      <p id={helpId} class="text-xs text-muted-foreground">
        Your own access ends on {ceilingDate}. The access you give ends then, or on an earlier day
        that you choose.
      </p>
    {:else}
      <p id={helpId} class="text-xs text-muted-foreground">
        Access stays through the end of this day. Leave it empty for no end date.
      </p>
    {/if}
  </div>
{/if}
