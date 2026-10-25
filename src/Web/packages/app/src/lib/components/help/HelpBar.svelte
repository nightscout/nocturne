<script lang="ts">
  import { page } from "$app/state";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import X from "@lucide/svelte/icons/x";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { Button } from "$lib/components/ui/button";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import { articleFor, documentationLinks } from "$lib/help/documentation.svelte";
  const help = getHelpContext();
  const article = $derived(
    articleFor(page.url.pathname, documentationLinks(help?.navigation ?? []))
  );
</script>

{#if help?.ready && help.mode === "bar" && !help.open}
  <aside
    aria-label="Page help"
    class="print:hidden flex items-center gap-3 border-b bg-primary/5 px-4 py-2.5"
  >
    <BookOpen class="size-4 shrink-0 text-primary" />
    <p class="min-w-0 flex-1 text-sm">
      <span class="font-medium">Help with {article.title}</span>
      <span class="hidden text-muted-foreground lg:inline">
       : Explanations, settings, and getting started
      </span>
    </p>
    <Button
      size="sm"
      variant="ghost"
      onclick={() => help.show(page.url.pathname)}
    >
      Open help <ArrowRight class="size-4" />
    </Button>
    <Button
      variant="ghost"
      size="icon"
      title="Minimize help to library icon"
      aria-label="Minimize help to library icon"
      onclick={() => help.setMode("icon")}
    >
      <X class="size-4" />
    </Button>
  </aside>
{/if}
