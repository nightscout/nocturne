<script lang="ts">
  import { Check } from "lucide-svelte";

  let {
    path,
    currentStep,
    steps,
    onJumpToStep,
  }: {
    path: "fresh" | "migration";
    currentStep: number;
    steps: Array<{ id: string; label: string }>;
    onJumpToStep: (index: number) => void;
  } = $props();
</script>

<nav class="flex flex-col gap-8">
  <!-- Eyebrow -->
  <p
    class="flex items-center gap-2 font-mono text-xs uppercase tracking-widest text-(--onb-accent)"
  >
    <span
      class="inline-block h-1.5 w-1.5 rounded-full bg-(--onb-accent) shadow-(--onb-glow-accent)"
    ></span>
    {#if path === "fresh"}
      Welcome to Nocturne
    {:else}
      Migrating from Nightscout
    {/if}
  </p>

  <!-- Display heading -->
  <h2
    class="font-[Montserrat] text-4xl font-[250] leading-tight -tracking-[0.02em] text-white"
  >
    {#if path === "fresh"}
      Let's get your data <em class="not-italic text-(--onb-accent)">flowing.</em>
    {:else}
      Bring your <em class="not-italic text-(--onb-accent)">decade</em> of data with you.
    {/if}
  </h2>

  <!-- Supporting paragraph -->
  <p class="text-[14.5px] leading-relaxed text-muted-foreground">
    {#if path === "fresh"}
      Four short steps. Nothing to uninstall later, nothing sent off-server.
      You can change every choice in Settings afterwards.
    {:else}
      We'll connect to your Nightscout instance, copy your entries over, and
      keep your uploaders pointing at the new host. No data loss, no downtime.
    {/if}
  </p>

  <!-- Step list -->
  <ol class="flex flex-col">
    {#each steps as step, index (step.id)}
      {@const isDone = index < currentStep}
      {@const isCurrent = index === currentStep}
      {@const isFuture = index > currentStep}
      {@const isLast = index === steps.length - 1}

      <li class="flex gap-3">
        <!-- Circle + connector column -->
        <div class="flex flex-col items-center">
          <!-- Circle -->
          <button
            type="button"
            onclick={() => onJumpToStep(index)}
            class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm transition-all
              {isDone ? 'bg-(--onb-accent) text-white' : ''}
              {isCurrent ? 'border-2 border-(--onb-accent) text-white shadow-(--onb-step-glow)' : ''}
              {isFuture ? 'border border-(--onb-accent-dim) text-muted-foreground' : ''}"
          >
            {#if isDone}
              <Check class="h-4 w-4" />
            {:else}
              {index + 1}
            {/if}
          </button>

          <!-- Connector line -->
          {#if !isLast}
            <div
              class="w-px grow my-1 min-h-4 bg-(--onb-accent-dim)"
            ></div>
          {/if}
        </div>

        <!-- Label -->
        <button
          type="button"
          onclick={() => onJumpToStep(index)}
          class="pt-1 text-left text-sm transition-colors
            {isDone ? 'text-muted-foreground' : ''}
            {isCurrent ? 'font-medium text-white' : ''}
            {isFuture ? 'text-muted-foreground' : ''}"
        >
          {step.label}
        </button>
      </li>
    {/each}
  </ol>
</nav>
