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

<!-- A finished step is washed over: the fill spreads across the number from its lower left edge,
     like wet pigment, and the line to the next step fills after it. -->
{#snippet marker(index: number)}
  {@const done = index < current}
  <span
    class="relative flex size-6 shrink-0 items-center justify-center overflow-hidden rounded-full border text-xs tabular-nums transition-colors duration-200 motion-reduce:transition-none
      {done ? 'border-primary' : ''}
      {index === current ? 'border-2 border-primary text-foreground' : ''}
      {index > current ? 'border-border' : ''}"
  >
    <span
      class="absolute -inset-1 origin-bottom-left rounded-full bg-primary transition-transform duration-500 ease-out motion-reduce:transition-none {done
        ? 'scale-100'
        : 'scale-0'}"
      aria-hidden="true"
    ></span>
    <span
      class="relative transition-opacity duration-200 motion-reduce:transition-none {done
        ? 'opacity-0'
        : 'opacity-100'}"
    >
      {index + 1}
    </span>
    <span
      class="absolute inset-0 flex items-center justify-center text-primary-foreground transition-all duration-300 ease-out motion-reduce:transition-none {done
        ? 'scale-100 opacity-100 delay-200'
        : 'scale-50 opacity-0'}"
    >
      <Check class="size-3.5" aria-hidden="true" />
    </span>
    {#if done}
      <span class="sr-only">Done:</span>
    {/if}
  </span>
{/snippet}

{#snippet track(filled: boolean, shape: string)}
  <span class="flex-1 overflow-hidden bg-border {shape}" aria-hidden="true">
    <span
      class="block h-full origin-left bg-primary transition-transform duration-500 ease-out motion-reduce:transition-none rtl:origin-right {filled
        ? 'scale-x-100 delay-150'
        : 'scale-x-0'}"
    ></span>
  </span>
{/snippet}

<nav aria-label="Steps">
  <!-- Narrow: the position in words and a bar. Wide: every step by name. -->
  <div class="@lg:hidden">
    <p class="text-sm text-muted-foreground" aria-live="polite">
      Step {current + 1} of {steps.length}:
      <span class="font-medium text-foreground">{steps[current]?.label}</span>
    </p>
    <div class="mt-2 flex gap-1">
      {#each steps as step, index (step.id)}
        {@render track(index <= current, "h-1 rounded-full")}
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
            class="flex min-w-0 items-center gap-2 text-sm transition-colors duration-200 {isCurrent
              ? 'font-medium text-foreground'
              : 'text-muted-foreground'}"
            aria-current={isCurrent ? "step" : undefined}
          >
            {@render marker(index)}
            <span class="truncate">{step.label}</span>
          </span>
        {/if}
        {#if index < steps.length - 1}
          {@render track(index < current, "h-px min-w-4")}
        {/if}
      </li>
    {/each}
  </ol>
</nav>
