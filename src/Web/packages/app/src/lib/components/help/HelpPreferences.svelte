<script lang="ts">
  import BookOpen from "@lucide/svelte/icons/book-open";
  import * as Select from "$lib/components/ui/select";
  import { getHelpContext, isHelpMode } from "$lib/help/help-context.svelte";
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
  </section>
{/if}
