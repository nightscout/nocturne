<script lang="ts">
  import manifest from 'virtual:blog-manifest';
</script>

<div class="container mx-auto px-4 py-8 max-w-3xl">
  <h1 class="text-4xl font-bold tracking-tight mb-8">Blog</h1>

  <div class="space-y-8">
    {#each manifest.posts as post}
      <article class="border-b border-border/40 pb-8">
        <time class="text-sm text-muted-foreground">{post.date}</time>
        <h2 class="text-2xl font-bold mt-1 mb-2">
          <a href="/blog/{post.slug}" class="hover:text-primary transition-colors">
            {post.title}
          </a>
        </h2>
        <p class="text-muted-foreground mb-3">{post.summary}</p>
        <div class="flex items-center gap-4">
          <span class="text-sm text-muted-foreground">By {post.author}</span>
          <div class="flex gap-2">
            {#each post.tags as tag}
              <span class="text-xs px-2 py-0.5 rounded-full bg-muted text-muted-foreground">{tag}</span>
            {/each}
          </div>
        </div>
      </article>
    {/each}
  </div>
</div>
