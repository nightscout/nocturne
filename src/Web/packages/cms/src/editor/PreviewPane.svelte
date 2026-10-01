<script lang="ts">
  import type { Component } from 'svelte';

  interface PreviewSegment {
    type: 'html' | 'component';
    content: string;
    componentName?: string;
    props?: Record<string, unknown>;
  }

  let {
    content = '',
    mode = 'markdown',
    componentMap = {},
  }: {
    content: string;
    mode: 'markdown' | 'email';
    componentMap?: Record<string, Component>;
  } = $props();

  /**
   * Split HTML content at component placeholder boundaries.
   * Component placeholders are: <div data-svelte-component="Name" ...>Name</div>
   */
  const segments = $derived.by((): PreviewSegment[] => {
    if (!content) return [];

    const regex = /<div[^>]*data-svelte-component="([^"]+)"[^>]*>[\s\S]*?<\/div>/g;
    const result: PreviewSegment[] = [];
    let lastIndex = 0;
    let match: RegExpExecArray | null;

    while ((match = regex.exec(content)) !== null) {
      // HTML before this component
      if (match.index > lastIndex) {
        result.push({ type: 'html', content: content.slice(lastIndex, match.index) });
      }

      // The component itself
      const componentName = match[1];
      // Extract props from data-component-props attribute
      const propsMatch = match[0].match(/data-component-props="([^"]*)"/);
      let props: Record<string, unknown> = {};
      if (propsMatch) {
        try {
          // The JSON is HTML-encoded in the attribute (quotes become &quot;)
          const decoded = propsMatch[1].replace(/&quot;/g, '"').replace(/&amp;/g, '&');
          props = JSON.parse(decoded);
        } catch { /* ignore parse errors */ }
      }

      if (componentMap[componentName]) {
        result.push({ type: 'component', content: '', componentName, props });
      } else {
        // No real component registered — show placeholder
        result.push({ type: 'html', content: match[0] });
      }

      lastIndex = match.index + match[0].length;
    }

    // Remaining HTML after last component
    if (lastIndex < content.length) {
      result.push({ type: 'html', content: content.slice(lastIndex) });
    }

    return result;
  });
</script>

<div class="sticky top-0 flex h-screen w-[400px] shrink-0 flex-col border-l border-border/40 overflow-hidden">
  <div class="flex items-center border-b border-border/40 px-4 py-2">
    <span class="text-sm font-medium text-muted-foreground">Preview</span>
  </div>

  <div class="flex-1 overflow-y-auto p-6">
    {#if mode === 'markdown'}
      <article class="prose prose-neutral dark:prose-invert max-w-none">
        {#each segments as segment, i (i)}
          {#if segment.type === 'html'}
            <!-- eslint-disable-next-line svelte/no-at-html-tags -- editor.getHTML() output: schema-serialised with text escaped; the one scriptable attribute, iframe src, is filtered by safeFrameSrc -->
            {@html segment.content}
          {:else if segment.type === 'component' && segment.componentName && componentMap[segment.componentName]}
            <div class="not-prose my-4">
              <svelte:component this={componentMap[segment.componentName]} {...segment.props} />
            </div>
          {/if}
        {/each}
      </article>
    {:else}
      <div class="rounded-lg border border-border/40 bg-muted/20 p-4">
        <p class="text-sm text-muted-foreground">Email preview will use better-svelte-email renderer.</p>
        <article class="mt-4 prose prose-neutral dark:prose-invert max-w-none">
          <!-- eslint-disable-next-line svelte/no-at-html-tags -- editor.getHTML() output, as above -->
          {@html content}
        </article>
      </div>
    {/if}
  </div>
</div>
