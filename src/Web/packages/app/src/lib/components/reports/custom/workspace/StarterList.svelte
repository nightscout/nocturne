<!--
  The start of a new report: the starting questions as ruled lists under plain headings, and a
  blank report.
-->
<script lang="ts">
  import type { CustomReportTemplate } from "#lib/api/generated/nocturne-api-client.js";
  import * as Item from "#lib/components/ui/item/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import { minuteLabel } from "./clock.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { STARTERS, STARTERS_SHOWN, STARTER_GROUPS } from "./starters.js";
  import { starterGroupLabel, starterWords } from "./vocabulary-words.svelte.js";
  import type { Starter } from "./workspace.svelte.js";

  interface Props {
    onstart: (starter: Starter | null) => void;
    /** The API's ready-made reports, which open in the form; listed last. */
    templates?: CustomReportTemplate[];
    ontemplate?: (template: CustomReportTemplate) => void;
  }

  let { onstart, templates = [], ontemplate }: Props = $props();
</script>

{#snippet starterRow(starter: Starter)}
  {@const words = starterWords(minuteLabel)[starter.id]}
  {@render row(words.title, words.description, () => onstart(starter))}
{/snippet}

{#snippet row(title: string, description: string, start: () => void)}
  <li class="py-1">
    <Item.Root variant="ghost" onclick={start}>
      <Item.Content>
        <Item.Title>{title}</Item.Title>
        <Item.Description>{description}</Item.Description>
      </Item.Content>
      <ChevronRight class="size-4 text-muted-foreground" aria-hidden="true" />
    </Item.Root>
  </li>
{/snippet}

<div class="max-w-3xl space-y-6" data-testid="report-starters">
  <div class="space-y-1">
    <h2 class="text-xl font-semibold">Start with a question</h2>
    <p class="text-sm text-muted-foreground">
      Choose a question to start from. You can change everything in it after.
    </p>
  </div>
  {#each STARTER_GROUPS as group, g (group)}
    {@const all = STARTERS.filter((s) => s.group === group)}
    <section class="space-y-1" aria-labelledby="starters-{g}">
      <h3 id="starters-{g}" class="text-sm font-medium text-muted-foreground">
        {starterGroupLabel(group)}
      </h3>
      <ul class="divide-y divide-border border-y border-border">
        {#each all.slice(0, STARTERS_SHOWN) as starter (starter.id)}
          {@render starterRow(starter)}
        {/each}
      </ul>
      {#if all.length > STARTERS_SHOWN}
        <Collapsible.Root>
          <Collapsible.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="subtle" size="xs" class="group">
                <ChevronRight
                  class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                  aria-hidden="true"
                />
                More questions ({all.length - STARTERS_SHOWN})
              </Button>
            {/snippet}
          </Collapsible.Trigger>
          <Collapsible.Content>
            <ul class="divide-y divide-border border-b border-border">
              {#each all.slice(STARTERS_SHOWN) as starter (starter.id)}
                {@render starterRow(starter)}
              {/each}
            </ul>
          </Collapsible.Content>
        </Collapsible.Root>
      {/if}
    </section>
  {/each}
  {#if templates.length > 0 && ontemplate}
    <section class="space-y-1" aria-labelledby="starters-templates" data-testid="ready-made-reports">
      <h3 id="starters-templates" class="text-sm font-medium text-muted-foreground">Ready-made reports</h3>
      <ul class="divide-y divide-border border-y border-border">
        {#each templates as template (template.id)}
          {@render row(template.name, template.description, () => ontemplate(template))}
        {/each}
      </ul>
    </section>
  {/if}
  <ul class="divide-y divide-border border-y border-border">
    {@render row(
      "Blank report",
      "A report with one empty chart. Select the signals that you want.",
      () => onstart(null)
    )}
  </ul>
</div>
