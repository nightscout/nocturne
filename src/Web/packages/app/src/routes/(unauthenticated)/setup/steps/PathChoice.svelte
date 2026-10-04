<script lang="ts">
  import Check from "@lucide/svelte/icons/check";
  import Sprout from "@lucide/svelte/icons/sprout";
  import Cable from "@lucide/svelte/icons/cable";
  import * as RadioGroup from "$lib/components/ui/radio-group";

  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  interface Props {
    path: "fresh" | "migration";
    voice: PatientVoice;
  }

  let { path = $bindable(), voice }: Props = $props();

  const PATHS = $derived([
    {
      value: "fresh",
      tag: "Fresh start",
      icon: Sprout,
      title: "Start with a blank slate",
      description:
        "I'm new to open-source diabetes data, or I'd rather not bring old data with me.",
      bullets: [
        "Connect a CGM, pump, or phone app",
        "Skip anything and set it up later",
        voice.kind === "self"
          ? "Land on your dashboard"
          : voice.kind === "named"
            ? `Land on ${voice.name}'s dashboard`
            : "Land on the dashboard",
      ],
    },
    {
      value: "migration",
      tag: "Coming from Nightscout",
      icon: Cable,
      title:
        voice.kind === "self"
          ? "Migrate my Nightscout data"
          : voice.kind === "named"
            ? `Migrate ${voice.name}'s Nightscout data`
            : "Migrate Nightscout data",
      description:
        voice.kind === "self"
          ? "I already run Nightscout. Copy my history across without changing my Nightscout site."
          : voice.kind === "named"
            ? `${voice.name} already has a Nightscout site. Copy ${voice.name}'s history across without changing that site.`
            : "There's already a Nightscout site. Copy its history across without changing that site.",
      bullets: [
        "Point at the existing Nightscout address",
        "Import entries, treatments, and profiles",
        "Keep Nightscout running while you try Nocturne",
      ],
    },
  ] as const);
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <div class="flex flex-col items-center gap-4 text-center">
    <!-- prettier-ignore -->
    <h1
      id="path-choice-heading"
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl md:text-4xl xl:text-5xl"
    >
      How are you <em class="not-italic font-light text-primary">arriving</em>?
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
      Both roads end in the same place. We just want to know whether to carry
      existing Nightscout data over, or give you a clean notebook to start
      in.
    </p>
  </div>

  <RadioGroup.Root
    class="w-full max-w-170 grid-cols-1 gap-5 sm:grid-cols-2"
    aria-labelledby="path-choice-heading"
    value={path}
    onValueChange={(value) => {
      if (value === "fresh" || value === "migration") path = value;
    }}
  >
    {#each PATHS as option (option.value)}
      <RadioGroup.Card value={option.value}>
        {#snippet children({ checked })}
          <span class="flex w-full flex-col gap-5 p-2">
            <span
              class="absolute right-4 top-4 block h-5 w-5 rounded-full border-2 transition-colors {checked
                ? 'border-primary bg-primary inset-ring-4 inset-ring-card'
                : 'border-border'}"
            ></span>

            <span
              class="inline-flex self-start rounded-full bg-primary/10 px-2.5 py-0.5 font-mono text-2xs uppercase tracking-wider text-primary"
            >
              {option.tag}
            </span>

            <span
              class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
            >
              <option.icon class="h-6 w-6 text-primary" />
            </span>

            <span class="font-brand text-xl font-normal leading-snug text-foreground">
              {option.title}
            </span>

            <span class="text-sm leading-relaxed text-muted-foreground">
              {option.description}
            </span>

            <span class="flex flex-col gap-2.5">
              {#each option.bullets as bullet (bullet)}
                <span class="flex items-start gap-2.5 text-sm text-foreground">
                  <Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                  {bullet}
                </span>
              {/each}
            </span>
          </span>
        {/snippet}
      </RadioGroup.Card>
    {/each}
  </RadioGroup.Root>
</div>
