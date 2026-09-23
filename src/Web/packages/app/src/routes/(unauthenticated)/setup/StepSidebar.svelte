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
    class="flex items-center gap-2 font-mono text-[11px] uppercase tracking-widest"
    style="color: var(--onb-accent);"
  >
    <span
      class="inline-block h-1.5 w-1.5 rounded-full"
      style="background: var(--onb-accent); box-shadow: 0 0 10px var(--onb-accent);"
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
      Let's get your data <em class="not-italic" style="color: var(--onb-accent);">flowing.</em>
    {:else}
      Bring your <em class="not-italic" style="color: var(--onb-accent);">decade</em> of data with you.
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
    {#each steps as step, index}
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
              {isDone ? 'text-white' : ''}
              {isCurrent ? 'border-2 text-white' : ''}
              {isFuture ? 'border text-muted-foreground' : ''}"
            style={isDone
              ? `background: var(--onb-accent);`
              : isCurrent
                ? `border-color: var(--onb-accent); box-shadow: 0 0 12px var(--onb-accent-dim);`
                : `border-color: var(--onb-accent-dim);`}
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
              class="w-px grow my-1 min-h-4"
              style="background: var(--onb-accent-dim);"
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
