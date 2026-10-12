<script lang="ts">
  import HelpReference from "./HelpReference.svelte";
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
  } from "$lib/help/documentation.svelte";
  const help = getHelpContext();
  let query = $state("");
  $effect(() => {
    if (help?.open) query = "";
  });
  $effect(() => {
    if (help?.path) {
      query = "";
      if (help.layout === "page")
        window.scrollTo({ top: 0, behavior: "instant" });
    }
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

<section
  id="nocturne-page-help"
  aria-label="Page documentation"
  class="container mx-auto max-w-5xl space-y-6 p-4 sm:p-6"
>
  <div class="flex items-center gap-3">
    <BookOpen class="size-5 shrink-0 text-primary" />
    <p class="font-medium">Documentation</p>
  </div>
  <InputGroup.Root>
    <InputGroup.Addon>
      <Search class="size-4" />
    </InputGroup.Addon><InputGroup.Input
      aria-label="Search all documentation"
      placeholder="Search all documentation…"
      bind:value={query}
    />
  </InputGroup.Root>
  <div class="space-y-6">
    {#if query.trim()}
      <h1 class="text-2xl font-bold">Search results</h1>
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
          No documentation found. Try a page name, a setting, or a connector.
        </p>
      {/each}
    {:else}
      <div class="space-y-3">
        <p class="text-xs font-semibold uppercase tracking-widest text-primary">
          Page guide
        </p>
        <h1 class="text-3xl font-bold tracking-tight">
          {article.title}
        </h1>
        <p class="text-muted-foreground leading-relaxed">
          {article.introduction}
        </p>
      </div>
      {#if article.sources?.length}
        <section class="space-y-2 rounded-xl border p-5">
          <h2 class="font-semibold">Official instructions and sources</h2>
          <p class="text-sm text-muted-foreground">
            Provider screens and access rules can change. Check the original
            instructions alongside this Nocturne guide.
          </p>
          <!-- eslint-disable svelte/no-navigation-without-resolve -- Official provider links are external URLs. -->
          {#each article.sources as source (source.href)}
            <a
              class="block text-sm text-primary underline underline-offset-4"
              href={source.href}
              target="_blank"
              rel="noopener noreferrer"
            >
              {source.title}
            </a>
          {/each}
          <!-- eslint-enable svelte/no-navigation-without-resolve -->
        </section>
      {/if}
      {#if article.reference}<HelpReference kind={article.reference} />{/if}
      {#if article.steps?.length}
        <section class="space-y-4" aria-label="Step-by-step setup">
          <h2 class="font-semibold">Step-by-step setup</h2>
          <ol class="space-y-4">
            {#each article.steps as step, i (step.title)}
              <li class="flex gap-4 rounded-xl border p-5">
                <span
                  class="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground text-sm font-semibold"
                  aria-hidden="true"
                >
                  {i + 1}
                </span>
                <div class="space-y-2">
                  <h3 class="font-semibold">{step.title}</h3>
                  <p class="text-sm leading-relaxed text-muted-foreground">
                    {step.text}
                  </p>
                </div>
              </li>
            {/each}
          </ol>
        </section>
      {/if}
      {#each article.sections as section (section.title)}
        <section class="space-y-2 rounded-xl border p-5">
          <h2 class="font-semibold">{section.title}</h2>
          <p class="text-sm leading-relaxed text-muted-foreground">
            {section.text}
          </p>
        </section>
      {/each}
      {#if related.length}
        <section class="space-y-3">
          <h2 class="font-semibold">Continue exploring</h2>
          {#each related as path (path)}
            <Button
              class="w-full justify-between"
              variant="outline"
              onclick={() => select(path)}
            >
              {articleFor(path, links).title}<ArrowRight class="size-4" />
            </Button>
          {/each}
        </section>
      {/if}
      {#if article.href.startsWith("/settings/") && links.some((link) => link.href === article.href)}
        <Button href={article.href} target="_blank" rel="noopener">
          {#if article.href.startsWith("/settings/connectors/")}Open connector
            settings{:else}Open settings in a new tab{/if}
          <ArrowRight class="size-4" />
        </Button>
      {/if}
    {/if}
  </div>
</section>
