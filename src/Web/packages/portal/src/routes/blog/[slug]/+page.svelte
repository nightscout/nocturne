<script lang="ts">
  let { data } = $props();
</script>

<svelte:head>
  <title>{data.meta?.title} - Nocturne Blog</title>
  <meta name="description" content={data.meta?.summary} />
  {#if data.meta?.image}
    <meta property="og:image" content={data.meta.image} />
  {/if}
</svelte:head>

<div class="container mx-auto px-4 py-8 max-w-3xl">
  <article class="prose prose-neutral dark:prose-invert max-w-none">
    <header class="mb-8 not-prose">
      <h1 class="text-4xl font-bold tracking-tight mb-3">{data.meta?.title}</h1>
      <p class="text-sm text-muted-foreground">
        <time datetime={data.meta?.date}>{data.meta?.date}</time> &middot; By {data.meta?.author}
      </p>
    </header>

    <data.content />
  </article>
</div>
