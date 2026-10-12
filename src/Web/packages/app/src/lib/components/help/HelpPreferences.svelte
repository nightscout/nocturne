<script lang="ts">
  import BookOpen from "@lucide/svelte/icons/book-open";
  import HelpAppearanceControls from "./HelpAppearanceControls.svelte";
  import * as Select from "$lib/components/ui/select";
  import {
    getHelpContext,
    isHelpMode,
    isHelpLayout,
    helpLayouts,
    helpLayoutLabel,
  } from "$lib/help/help-context.svelte";
  const help = getHelpContext();
</script>

{#if help}
  <section
    class="rounded-xl border bg-card p-6 space-y-3"
    aria-labelledby="help-preferences-title"
  >
    <h2
      id="help-preferences-title"
      class="flex items-center gap-2 font-semibold"
    >
      <BookOpen class="size-5" />Documentation & help
    </h2>
    <p class="text-sm text-muted-foreground">
      Choose how to access page help. This preference is saved in this browser.
    </p>
    <label class="block text-sm font-medium" for="help-visibility">
      Help visibility
    </label>
    <Select.Root
      type="single"
      value={help.mode}
      onValueChange={(value) => {
        if (isHelpMode(value)) help.setMode(value);
      }}
    >
      <Select.Trigger id="help-visibility" class="w-full sm:max-w-sm">
        {help.mode === "bar"
          ? "Full help bar"
          : help.mode === "icon"
            ? "Library icon only"
            : "Hidden"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="bar">Full help bar</Select.Item>
        <Select.Item value="icon">Library icon only</Select.Item>
        <Select.Item value="hidden">Hidden</Select.Item>
      </Select.Content>
    </Select.Root>
    <p class="text-sm text-muted-foreground">
      When hidden, help can always be enabled again here.
    </p>
    <label class="block text-sm font-medium" for="help-layout">
      Documentation layout
    </label>
    <Select.Root
      type="single"
      value={help.layout}
      onValueChange={(value) => {
        if (isHelpLayout(value)) help.setLayout(value);
      }}
    >
      <Select.Trigger id="help-layout" class="w-full sm:max-w-sm">
        {helpLayoutLabel(help.layout)}
      </Select.Trigger>
      <Select.Content>
        {#each helpLayouts as layout (layout)}
          <Select.Item value={layout}>{helpLayoutLabel(layout)}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
    <p class="text-sm text-muted-foreground">
      {#if help.layout === "drawer"}
        Opens like a transparent book from the boundary between menu and page. Your existing navigation stays in place and browses documentation while help is open.
      {:else if help.layout === "right"}
        Opens a narrower drawer on the right, with guide navigation inside the
        panel.
      {:else if help.layout === "alongside"}
        Keeps the page usable beside its guide. On smaller screens, opens a
        full-width drawer.
      {:else}
        Replaces the page content temporarily and uses the existing menu for
        documentation.
      {/if}
      Close help with the book beside Nocturne, the close button, or Escape. Your
      page and its inputs stay in place.
    </p>
    <HelpAppearanceControls />
  </section>
{/if}
