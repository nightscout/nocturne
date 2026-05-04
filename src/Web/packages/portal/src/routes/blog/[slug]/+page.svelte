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
    <div class="mb-8 not-prose">
      <time class="text-sm text-muted-foreground">{data.meta?.date}</time>
      <p class="text-sm text-muted-foreground mt-1">By {data.meta?.author}</p>
    </div>

    <data.content />
  </article>
</div>
