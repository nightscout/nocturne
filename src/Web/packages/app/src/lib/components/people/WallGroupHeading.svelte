<script lang="ts">
  import GroupName from "./GroupName.svelte";
  import type { WallHeading } from "./wall-groups.js";
  import type { WallStep } from "./wall-layout.js";

  interface Props {
    heading: WallHeading;
    step: WallStep;
    /** A heading repeated over a continued column; assistive technology skips it so the group is announced once. */
    repeated: boolean;
  }

  const { heading, step, repeated }: Props = $props();
</script>

<li
  class="flex min-w-0 items-end border-b px-1 pb-1 {step.rowClass}"
  aria-hidden={repeated ? "true" : undefined}
  data-testid="wall-heading"
>
  <h2 class="min-w-0 truncate font-semibold text-muted-foreground {step.nameClass}">
    {#if heading.group}
      <GroupName group={heading.group} />
    {:else}
      Not in your groups
    {/if}
  </h2>
</li>
