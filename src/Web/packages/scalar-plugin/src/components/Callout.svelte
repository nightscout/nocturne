<script lang="ts" module>
  export const CALLOUT_TYPES = ['info', 'tip', 'warning', 'danger'] as const;
  export type CalloutType = (typeof CALLOUT_TYPES)[number];
</script>

<script lang="ts">
  import { parseInline } from '../blocks';

  let { type = 'info', body }: { type?: CalloutType; body: string } = $props();

  const LABELS: Record<CalloutType, string> = { info: 'Note', tip: 'Tip', warning: 'Warning', danger: 'Danger' };

  const paragraphs = $derived(body.split(/\n\s*\n/).map((paragraph) => parseInline(paragraph.replace(/\n/g, ' '))));
</script>

<div class="callout {type}" role="note">
  {#each paragraphs as paragraph, index}
    <p>
      {#if index === 0}<strong class="label">{LABELS[type]}:</strong>{/if}
      {#each paragraph as token}
        {#if token.kind === 'code'}<code>{token.text}</code>
        {:else if token.kind === 'strong'}<strong>{token.text}</strong>
        {:else if token.kind === 'link'}<a href={token.href}>{token.text}</a>
        {:else}{token.text}{/if}
      {/each}
    </p>
  {/each}
</div>

<style>
  .callout {
    --accent: var(--scalar-color-blue, #3b82f6);
    margin: 1rem 0;
    padding: 0.75rem 1rem;
    border: var(--scalar-border-width, 1px) solid color-mix(in srgb, var(--accent) 35%, transparent);
    border-radius: var(--scalar-radius, 6px);
    background: color-mix(in srgb, var(--accent) 6%, transparent);
    color: var(--scalar-color-1);
  }
  .tip { --accent: var(--scalar-color-green, #16a34a); }
  .warning { --accent: var(--scalar-color-orange, #d97706); }
  .danger { --accent: var(--scalar-color-red, #dc2626); }
  p { margin: 0; line-height: 1.6; }
  p + p { margin-top: 0.5rem; }
  code {
    font-family: var(--scalar-font-code, monospace);
    font-size: 0.9em;
    padding: 0.1em 0.3em;
    border-radius: 4px;
    background: var(--scalar-background-2);
  }
  a { color: inherit; text-decoration: underline; text-underline-offset: 3px; }
</style>
