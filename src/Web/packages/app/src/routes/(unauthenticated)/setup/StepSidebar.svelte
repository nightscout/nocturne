<script lang="ts">
  import { Check } from "lucide-svelte";
  import StepArtwork, { type StepArt } from "./StepArtwork.svelte";

  let {
    path,
    currentStep,
    steps,
    art,
    artProgress,
    onJumpToStep,
  }: {
    path: "fresh" | "migration";
    currentStep: number;
    steps: readonly { id: string; label: string }[];
    art: StepArt;
    /** See {@link StepArtwork}'s `progress`. */
    artProgress?: number;
    onJumpToStep: (index: number) => void;
  } = $props();
</script>

<nav class="flex flex-col gap-8">
  <StepArtwork
    {art}
    progress={artProgress}
    class="size-40 max-[900px]:size-24"
  />

  <p
    class="flex items-center gap-2 font-mono text-xs uppercase tracking-widest text-primary"
  >
    <span class="inline-block h-1.5 w-1.5 rounded-full bg-primary"></span>
    {#if path === "fresh"}
      Welcome to Nocturne
    {:else}
      Migrating from Nightscout
    {/if}
  </p>

  <h2
    class="font-brand text-4xl font-hairline leading-tight tracking-tight text-foreground"
  >
    {#if path === "fresh"}
      Let's get your data <em class="not-italic text-primary">flowing.</em>
    {:else}
      Bring your <em class="not-italic text-primary">history</em> with you.
    {/if}
  </h2>

  <p class="text-sm leading-relaxed text-muted-foreground">
    {#if path === "fresh"}
      A few short steps. Skip anything you're not ready for; you can change
      every choice in Settings afterwards.
    {:else}
      We'll connect to your Nightscout site and copy your history across. Your
      Nightscout site isn't changed, and your uploaders keep sending to it
      until you choose to move them.
    {/if}
  </p>

  <ol class="flex flex-col">
    {#each steps as step, index (step.id)}
      {@const isDone = index < currentStep}
      {@const isCurrent = index === currentStep}
      {@const isFuture = index > currentStep}
      {@const isLast = index === steps.length - 1}

      <li class="flex gap-3">
        <div class="flex flex-col items-center">
          <button
            type="button"
            onclick={() => onJumpToStep(index)}
            aria-current={isCurrent ? "step" : undefined}
            class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm transition-all
              {isDone ? 'bg-primary text-primary-foreground' : ''}
              {isCurrent ? 'border-2 border-primary text-foreground' : ''}
              {isFuture ? 'border border-border text-muted-foreground' : ''}"
          >
            {#if isDone}
              <Check class="h-4 w-4" />
            {:else}
              {index + 1}
            {/if}
          </button>

          {#if !isLast}
            <div class="w-px grow my-1 min-h-4 bg-border"></div>
          {/if}
        </div>

        <button
          type="button"
          onclick={() => onJumpToStep(index)}
          class="pt-1 text-left text-sm transition-colors
            {isCurrent ? 'font-medium text-foreground' : 'text-muted-foreground'}"
        >
          {step.label}
        </button>
      </li>
    {/each}
  </ol>
</nav>
