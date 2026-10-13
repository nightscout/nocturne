<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "$lib/components/ui/button";
  import X from "@lucide/svelte/icons/x";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import type { DashboardTopic } from "$lib/help/dashboard-guide.svelte";
  let {
    topic,
    close,
    scenario = 0,
    chooseScenario,
    scenarioLabels,
    allowedSettings,
    ranges,
    focus,
  }: {
    topic: DashboardTopic;
    close: () => void;
    scenario?: number;
    chooseScenario?: (index: number) => void;
    scenarioLabels?: string[];
    allowedSettings: string[];
    ranges: () => void;
    focus?: { title: string; text: string };
  } = $props();
</script>

<section
  class="space-y-5 rounded-xl border bg-card p-5 text-card-foreground"
  aria-label="Widget explanation"
  aria-live="polite"
>
  <div class="flex items-center justify-between gap-3">
    <h3 class="text-xl font-semibold">{topic.title}</h3>
    <Button
      variant="ghost"
      size="icon"
      aria-label="Close explanation"
      onclick={close}
    >
      <X class="size-4" />
    </Button>
  </div>
  {#if chooseScenario && scenarioLabels}
    <div class="space-y-2">
      <h4 class="font-medium">Complete examples</h4>
      <div class="flex flex-wrap gap-2">
        {#each scenarioLabels as label, i (label)}
          <Button
            variant={scenario === i ? "default" : "outline"}
            aria-pressed={scenario === i}
            onclick={() => chooseScenario?.(i)}
          >
            {label}
          </Button>
        {/each}
      </div>
      <p class="text-xs text-muted-foreground">
        The selected example changes the demo above. Your own data and settings
        stay unchanged.
      </p>
    </div>
  {/if}
  {#if focus}<section class="space-y-2">
      <h4 class="font-semibold">{focus.title}</h4>
      <p class="text-sm text-muted-foreground">{focus.text}</p>
    </section>{/if}
  <div class="grid gap-5 sm:grid-cols-2">
    <section class="space-y-2">
      <h4 class="font-semibold">What do you see?</h4>
      <p class="text-sm leading-relaxed text-muted-foreground">
        {topic.meaning}
      </p>
    </section>
    <section class="space-y-2">
      <h4 class="font-semibold">Where does it come from?</h4>
      <p class="text-sm leading-relaxed text-muted-foreground">
        {topic.source}
      </p>
    </section>
    <section class="space-y-2">
      <h4 class="font-semibold">Make it appear</h4>
      <p class="text-sm leading-relaxed text-muted-foreground">
        {topic.availability}
      </p>
    </section>
    <section class="space-y-2">
      <h4 class="font-semibold">Details and missing data</h4>
      <p class="text-sm leading-relaxed text-muted-foreground">{topic.check}</p>
    </section>
  </div>
  <section class="space-y-2">
    <h4 class="font-semibold">Change your real settings</h4>
    <p class="text-sm text-muted-foreground">
      These links open the real settings in a new tab, outside this help demo.
    </p>
    <div class="flex flex-wrap gap-2">
      {#each topic.settings.filter( (path) => allowedSettings.includes(path) ) as path (path)}
        <Button
          variant="outline"
          href={resolve(path)}
          target="_blank"
          rel="noopener noreferrer"
        >
          {#if path === "/settings/appearance"}Appearance{:else if path === "/settings/profile"}Therapy{:else if path === "/settings/connectors"}Connectors
            & Apps{:else if path === "/settings/trackers"}Trackers{:else if path === "/settings/data-quality"}Data
            Quality{:else if path === "/alerts"}Alerts{:else}Patient Record{/if}
          <ExternalLink class="size-4" />
        </Button>
      {/each}
    </div>
    {#if ["glucose", "TirChart", "chart-ranges"].includes(topic.id)}
      <Button variant="link" onclick={ranges}>
        Glucose ranges and thresholds
      </Button>
    {/if}
  </section>
</section>
