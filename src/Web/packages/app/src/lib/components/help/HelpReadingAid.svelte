<script lang="ts">
  import type { HelpArticle } from "$lib/help/documentation-types";
  import * as Card from "$lib/components/ui/card";
  import MousePointer2 from "@lucide/svelte/icons/mouse-pointer-2";
  import Eye from "@lucide/svelte/icons/eye";
  import Settings2 from "@lucide/svelte/icons/settings-2";
  let { article }: { article: HelpArticle } = $props();
  const icons = [MousePointer2, Eye, Settings2];
  const heights = [25, 45, 70, 92, 76, 53, 34, 17];
</script>

{#if article.reading?.length}
  <section class="space-y-4" aria-label="Page reading aid">
    <h2 class="font-semibold">How to read this page</h2>
    {#if article.visual && article.visual !== "flow"}
      <figure class="space-y-2 rounded-xl border bg-muted/20 p-4">
        <svg
          viewBox="0 0 360 120"
          role="img"
          aria-label="Schematic reading example, without personal data"
          class="h-28 w-full text-primary"
          preserveAspectRatio="xMidYMid meet"
        >
          <path d="M20 10V100H345" fill="none" stroke="var(--border)" />
          {#if article.visual === "trend"}
            <path
              d="M25 75L58 60L94 66L125 34L151 48M184 56L213 75L244 66L281 40L336 51"
              stroke="currentColor"
              stroke-width="3"
              fill="none"
            />
            <path
              d="M168 12V98"
              stroke="var(--muted-foreground)"
              stroke-dasharray="3 4"
            />
            <circle cx="125" cy="34" r="4" fill="currentColor" />
            <circle cx="244" cy="66" r="4" fill="currentColor" />
          {:else if article.visual === "spread"}
            <path
              d="M25 45Q100 5 170 34T336 28L336 85Q250 108 170 77T25 94Z"
              fill="currentColor"
              fill-opacity="0.16"
            />
            <path
              d="M25 68Q100 30 170 53T336 56"
              stroke="currentColor"
              stroke-width="3"
              fill="none"
            />
          {:else if article.visual === "bars"}
            {#each heights as h, i (i)}<rect
                x={29 + i * 39}
                y={100 - h}
                width="25"
                height={h}
                rx="3"
                fill="currentColor"
                fill-opacity={0.4 + i * 0.065}
              />{/each}
          {:else if article.visual === "grid"}
            {#each Array.from({ length: 28 }, (_, i) => i) as i (i)}<rect
                x={32 + (i % 7) * 43}
                y={14 + Math.floor(i / 7) * 21}
                width="34"
                height="15"
                rx="3"
                fill={i === 12 ? "var(--muted)" : "currentColor"}
                fill-opacity={i === 12 ? 1 : 0.2 + ((i * 3) % 7) * 0.1}
              />{/each}
          {:else if article.visual === "scatter"}
            <path
              d="M30 92L333 15"
              stroke="var(--muted-foreground)"
              stroke-dasharray="4 4"
            />
            {#each [0, 1, 2, 3, 4, 5, 6, 7] as i (i)}<circle
                cx={45 + i * 39}
                cy={90 - i * 10 + ((i % 3) - 1) * 9}
                r="4"
                fill="currentColor"
              />{/each}
          {:else if article.visual === "stages"}
            <path
              d="M25 28H72V72H116V49H161V87H219V53H271V24H336"
              stroke="currentColor"
              stroke-width="5"
              fill="none"
            />
          {:else if article.visual === "stairs"}
            <path
              d="M25 80H89V47H162V63H242V31H336"
              stroke="currentColor"
              stroke-width="4"
              fill="none"
            />
            <path d="M25 97H336" stroke="var(--border)" />
          {/if}
        </svg>
        <figcaption class="text-xs text-muted-foreground">
          Reading diagram: illustrative, not your measurements. Colors follow
          your Nocturne theme.
        </figcaption>
      </figure>
    {/if}
    <div class="grid gap-3 sm:grid-cols-3">
      {#each article.reading as step, i (step.title)}
        {@const Icon = icons[i % icons.length]}
        <Card.Root variant="default">
          <Card.Header class="space-y-2 p-4 pb-2">
            <span
              class="inline-flex size-8 items-center justify-center rounded-full bg-primary/10 text-primary"
              aria-hidden="true"
            >
              <Icon class="size-4" />
            </span>
            <Card.Title class="text-sm">{i + 1}. {step.title}</Card.Title>
          </Card.Header>
          <Card.Content variant="muted" class="p-4 pt-0">
            <p class="text-sm leading-relaxed">
              {step.text}
            </p>
          </Card.Content>
        </Card.Root>
      {/each}
    </div>
  </section>
{/if}
