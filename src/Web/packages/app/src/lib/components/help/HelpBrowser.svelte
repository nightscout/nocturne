<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import * as InputGroup from "$lib/components/ui/input-group";
  import * as Item from "$lib/components/ui/item";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import Search from "@lucide/svelte/icons/search";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import {
    articleFor,
    documentationLinks,
    searchDocumentation,
  } from "$lib/help/documentation";
  const help = getHelpContext();
  let query = $state("");
  let content: HTMLDivElement | undefined = $state();
  $effect(() => {
    if (help?.open) query = "";
  });
  $effect(() => {
    if (help?.path) content?.scrollTo({ top: 0 });
  });
  const links = $derived(documentationLinks(help?.navigation ?? []));
  const article = $derived(articleFor(help?.path ?? "/", links));
  const results = $derived(searchDocumentation(query, links));
  const related = $derived(
    article.related.filter((path) => links.some((link) => link.href === path))
  );
  function select(path: string) {
    help?.select(path);
    query = "";
  }
</script>

{#if help}
  <Dialog.Root bind:open={help.open}>
    <Dialog.Content layout="workspace">
      <header
        class="flex items-center gap-3 border-b bg-primary/5 px-5 py-4 pr-12"
      >
        <BookOpen class="size-5 text-primary" />
        <div>
          <Dialog.Title>Nocturne documentation</Dialog.Title>
          <Dialog.Description>
            Explore help in the same layout as Nocturne. Close to return to your
            page.
          </Dialog.Description>
        </div>
      </header>
      <div class="flex min-h-0 flex-1 flex-col md:flex-row">
        <nav
          aria-label="Documentation navigation"
          class="max-h-44 w-full shrink-0 overflow-y-auto border-b bg-sidebar p-3 md:max-h-none md:w-60 md:border-r md:border-b-0"
        >
          {#each help.navigation as item (item.id)}
            {#if item.children}
              <details open class="mb-2">
                <summary
                  class="cursor-pointer rounded-md px-2 py-2 text-sm font-semibold"
                >
                  {item.title}
                </summary>
                {#each item.children as child (child.href)}
                  <Button
                    variant={article.href === child.href
                      ? "secondary"
                      : "ghost"}
                    size="menu"
                    onclick={() => select(child.href)}
                    aria-current={article.href === child.href
                      ? "page"
                      : undefined}
                  >
                    <child.icon class="size-4 shrink-0" />{child.title}
                  </Button>
                {/each}
              </details>
            {:else if item.href}
              <Button
                variant={article.href === item.href ? "secondary" : "ghost"}
                size="menu"
                onclick={() => select(item.href!)}
                aria-current={article.href === item.href ? "page" : undefined}
              >
                <item.icon class="size-4 shrink-0" />{item.title}
              </Button>
            {/if}
          {/each}
        </nav>
        <div bind:this={content} class="min-h-0 min-w-0 flex-1 overflow-y-auto">
          <div class="sticky top-0 z-10 border-b bg-background p-4">
            <InputGroup.Root>
              <InputGroup.Addon>
                <Search class="size-4" />
              </InputGroup.Addon><InputGroup.Input
                aria-label="Search all documentation"
                placeholder="Search all documentation…"
                bind:value={query}
              />
            </InputGroup.Root>
          </div>
          <div class="mx-auto max-w-3xl space-y-6 p-5 md:p-8">
            {#if query.trim()}
              <h2 class="text-xl font-semibold">Search results</h2>
              <p class="text-sm text-muted-foreground" role="status">
                {results.length} results
              </p>
              {#each results as result (result.href)}
                <Item.Root
                  variant="outline"
                  size="lg"
                  onclick={() => select(result.href)}
                >
                  <Item.Content>
                    <Item.Title>{result.title}</Item.Title><Item.Description>
                      {result.introduction}
                    </Item.Description>
                  </Item.Content>
                </Item.Root>
              {:else}
                <p class="text-muted-foreground">
                  No documentation found. Try a page name, a setting, or a
                  connector.
                </p>
              {/each}
            {:else}
              <div class="space-y-3">
                <p
                  class="text-xs font-semibold uppercase tracking-widest text-primary"
                >
                  Page guide
                </p>
                <h2 class="text-3xl font-bold tracking-tight">
                  {article.title}
                </h2>
                <p class="text-muted-foreground leading-relaxed">
                  {article.introduction}
                </p>
              </div>
              {#each article.sections as section (section.title)}
                <section class="space-y-2 rounded-xl border p-5">
                  <h3 class="font-semibold">{section.title}</h3>
                  <p class="text-sm leading-relaxed text-muted-foreground">
                    {section.text}
                  </p>
                </section>
              {/each}
              {#if related.length}
                <section class="space-y-3">
                  <h3 class="font-semibold">Continue exploring</h3>
                  {#each related as path (path)}
                    <Button
                      class="w-full justify-between"
                      variant="outline"
                      onclick={() => select(path)}
                    >
                      {articleFor(path, links).title}<ArrowRight
                        class="size-4"
                      />
                    </Button>
                  {/each}
                </section>
              {/if}
              {#if article.href.startsWith("/settings/connectors/") && links.some((link) => link.href === "/settings/connectors")}
                <Button href={article.href} onclick={() => (help.open = false)}>
                  Open connector settings <ArrowRight class="size-4" />
                </Button>
              {/if}
            {/if}
          </div>
        </div>
      </div>
      <footer
        class="flex items-center justify-between gap-3 border-t px-5 py-3 text-sm"
      >
        <span class="text-muted-foreground">
          Your Nocturne page stays where you left it.
        </span>
        <Button variant="outline" onclick={() => (help.open = false)}>
          Close help
        </Button>
      </footer>
    </Dialog.Content>
  </Dialog.Root>
{/if}
