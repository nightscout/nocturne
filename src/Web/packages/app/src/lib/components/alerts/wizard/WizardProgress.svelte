<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import Check from "@lucide/svelte/icons/check";

  interface Props {
    steps: readonly { id: string; label: string }[];
    current: number;
    /** The furthest step the person has reached; steps up to it can be opened. */
    reached: number;
    onJump: (index: number) => void;
  }

  let { steps, current, reached, onJump }: Props = $props();
</script>

{#snippet marker(index: number)}
  {@const done = index < current}
  <span
    class="flex size-6 shrink-0 items-center justify-center rounded-full text-xs tabular-nums transition-colors duration-200
      {done ? 'bg-primary text-primary-foreground' : ''}
      {index === current ? 'border-2 border-primary text-foreground' : ''}
      {index > current ? 'border border-border' : ''}"
  >
    {#if done}
      <Check class="size-3.5" aria-hidden="true" />
      <span class="sr-only">Done:</span>
    {:else}
      {index + 1}
    {/if}
  </span>
{/snippet}

<nav aria-label="Steps">
  <!-- Narrow: the position in words and a bar. Wide: every step by name. -->
  <div class="@lg:hidden">
    <p class="text-sm text-muted-foreground" aria-live="polite">
      Step {current + 1} of {steps.length}:
      <span class="font-medium text-foreground">{steps[current]?.label}</span>
    </p>
    <div class="mt-2 flex gap-1" aria-hidden="true">
      {#each steps as step, index (step.id)}
        <span
          class="h-1 flex-1 rounded-full transition-colors duration-200 {index <= current
            ? 'bg-primary'
            : 'bg-border'}"
        ></span>
      {/each}
    </div>
  </div>

  <ol class="hidden items-center gap-2 @lg:flex">
    {#each steps as step, index (step.id)}
      {@const isCurrent = index === current}
      <li class="flex min-w-0 items-center gap-2 {index < steps.length - 1 ? 'flex-1' : ''}">
        {#if index <= reached && !isCurrent}
          <Button variant="ghost" size="sm" class="-mx-2" onclick={() => onJump(index)}>
            {@render marker(index)}
            {step.label}
          </Button>
        {:else}
          <span
            class="flex min-w-0 items-center gap-2 text-sm {isCurrent
              ? 'font-medium text-foreground'
              : 'text-muted-foreground'}"
            aria-current={isCurrent ? "step" : undefined}
          >
            {@render marker(index)}
            <span class="truncate">{step.label}</span>
          </span>
        {/if}
        {#if index < steps.length - 1}
          <span
            class="h-px min-w-4 flex-1 transition-colors duration-200 {index < current
              ? 'bg-primary'
              : 'bg-border'}"
            aria-hidden="true"
          ></span>
        {/if}
      </li>
    {/each}
  </ol>
</nav>
