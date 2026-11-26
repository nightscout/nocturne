<script lang="ts">
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import {
    RULE_OWNER_APPROVES,
    RULE_TEAM_DECIDES,
    RULE_TEAM_TRUSTED,
  } from "#lib/teams/site-staff.svelte.js";

  interface Props {
    /** Makes the label id unique when the page shows more than one. */
    idPrefix: string;
    /** A TeamStaffRule value. */
    value: string;
    onChange: (staffRule: string) => void;
    disabled?: boolean;
    /** The team's staff see only the people in their groups, so a rule covers the staff in a group with this site. */
    narrows: boolean;
  }

  let { idPrefix, value, onChange, disabled = false, narrows }: Props = $props();

  const labelId = $derived(`${idPrefix}-staff-rule-label`);

  let root = $state<HTMLElement | null>(null);

  export function focusChecked() {
    root?.querySelector<HTMLElement>('[role="radio"][aria-checked="true"]')?.focus();
  }

  const choices = $derived([
    {
      rule: RULE_OWNER_APPROVES,
      label: "I approve each staff member",
      description: narrows
        ? "Each new staff member in a group with this site waits for your approval."
        : "Each new staff member waits for your approval.",
    },
    {
      rule: RULE_TEAM_DECIDES,
      label: "The team decides, and I hear about new staff",
      description: narrows
        ? "New staff in a group with this site can see it at once. You get a notice, and you can decline anyone."
        : "New staff can see this site at once. You get a notice, and you can decline anyone.",
    },
    {
      rule: RULE_TEAM_TRUSTED,
      label: "I trust the whole team",
      description: narrows
        ? "Every staff member in a group with this site can see it. You get no notices about them, and you cannot decline one person."
        : "Every staff member can see this site. You get no notices about them, and you cannot decline one person.",
    },
  ]);
</script>

<div class="space-y-2">
  <Label id={labelId}>Who decides which staff of the team can see this site</Label>
  <RadioGroup.Root
    bind:ref={root}
    class="gap-2"
    aria-labelledby={labelId}
    {value}
    onValueChange={onChange}
    {disabled}
  >
    {#each choices as choice (choice.rule)}
      <RadioGroup.Card value={choice.rule}>
        <span class="text-sm font-medium">{choice.label}</span>
        <span class="text-xs text-muted-foreground">{choice.description}</span>
      </RadioGroup.Card>
    {/each}
  </RadioGroup.Root>
</div>
