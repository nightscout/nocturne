<script lang="ts" module>
  /** The ceiling choice for everything the team holds at a site, which the API takes as no slug. */
  export const NO_CEILING = "*";

  export function ceilingSlug(choice: string): string | undefined {
    return choice === NO_CEILING ? undefined : choice;
  }
</script>

<script lang="ts">
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import AccessEndField from "#lib/components/members/AccessEndField.svelte";
  import type { TeamSuggestableRole } from "@nocturne/api-client";

  interface Props {
    id: string;
    roles: TeamSuggestableRole[];
    /** A role slug, or {@link NO_CEILING}. */
    ceiling: string;
    limitTo24Hours: boolean;
    /** The last day the staff member keeps access, as `YYYY-MM-DD`; empty for no end. */
    endDay: string;
    now: number;
    disabled?: boolean;
  }

  let {
    id,
    roles,
    ceiling = $bindable(NO_CEILING),
    limitTo24Hours = $bindable(false),
    endDay = $bindable(""),
    now,
    disabled = false,
  }: Props = $props();
</script>

<div class="space-y-2">
  <Label id={`${id}-ceiling-label`}>Most that the staff member can have at a site</Label>
  <RadioGroup.Root
    bind:value={ceiling}
    aria-labelledby={`${id}-ceiling-label`}
    class="gap-2"
    {disabled}
  >
    <RadioGroup.Card value={NO_CEILING}>
      <span class="text-sm font-medium">Everything the team has</span>
    </RadioGroup.Card>
    {#each roles as role (role.slug)}
      <RadioGroup.Card value={role.slug ?? ""}>
        <span class="text-sm font-medium">{role.name}</span>
      </RadioGroup.Card>
    {/each}
  </RadioGroup.Root>
  <p class="text-xs text-muted-foreground">
    A role limits the staff member to what the role gives and what the team has at the site.
  </p>
</div>

<div class="flex items-start gap-2 rounded-md border bg-muted/30 p-3">
  <Checkbox
    id={`${id}-limit`}
    checked={limitTo24Hours}
    onCheckedChange={(checked: boolean) => (limitTo24Hours = checked === true)}
    {disabled}
  />
  <div class="flex-1">
    <label for={`${id}-limit`} class="cursor-pointer text-sm font-medium select-none">
      Only the last 24 hours
    </label>
    <p class="mt-0.5 text-xs text-muted-foreground">
      The staff member can see only the most recent 24 hours of data.
    </p>
  </div>
</div>

<AccessEndField
  id={`${id}-end`}
  bind:value={endDay}
  {now}
  {disabled}
  label="End of the staff member's access (optional)"
/>
