<script lang="ts">
    import { base } from "$app/paths";
    import manifest from "@nocturne/screenshots/manifest.json";
    import type { Manifest, Theme } from "@nocturne/screenshots";

    interface Props {
        id: string;
        callouts?: { anchor: string; label: string }[];
        class?: string;
    }

    let { id, callouts = [], class: className = "" }: Props = $props();

    const themes: Theme[] = ["light", "dark"];
    const entries = manifest as Manifest;

    // A stale id or anchor throws rather than degrading, so a capture that dropped
    // the screenshot fails the prerendered build instead of shipping a broken page.
    const entry = $derived.by(() => {
        const found = entries[id];
        if (!found) {
            throw new Error(
                `Screenshot "${id}" is not in the screenshots manifest. Declare it in @nocturne/screenshots and re-run the capture, or correct the id.`,
            );
        }
        return found;
    });

    const chips = $derived(
        callouts.map(({ anchor, label }, index) => {
            const box = entry.anchors?.[anchor];
            if (!box) {
                const declared = Object.keys(entry.anchors ?? {});
                throw new Error(
                    `Screenshot "${id}" has no anchor "${anchor}". Declared anchors: ${declared.join(", ") || "none"}.`,
                );
            }
            // Either variant serves as the frame: capture rejects an anchored
            // entry whose variants differ in size or in any anchor box.
            const frame = entry.variants.light;
            return {
                label,
                number: index + 1,
                left: ((box.x + box.width / 2) / frame.width) * 100,
                top: ((box.y + box.height / 2) / frame.height) * 100,
            };
        }),
    );
</script>

<figure class="not-prose my-6 {className}">
    <div class="relative rounded-lg border border-border/60 bg-muted/30">
        {#each themes as theme (theme)}
            {@const variant = entry.variants[theme]}
            <img
                src="{base}/screenshots/{variant.file}"
                alt={entry.alt}
                width={variant.width}
                height={variant.height}
                loading="lazy"
                decoding="async"
                class="w-full h-auto rounded-lg {theme === 'light' ? 'block dark:hidden' : 'hidden dark:block'}"
            />
        {/each}

        {#each chips as chip}
            <span
                class="absolute -translate-x-1/2 -translate-y-1/2 flex items-center gap-1.5
                       rounded-full border border-border/60 bg-background/90 px-2 py-1
                       text-xs font-medium shadow-sm backdrop-blur-sm"
                style="left: {chip.left}%; top: {chip.top}%"
            >
                <span
                    class="size-4 shrink-0 rounded-full bg-primary text-primary-foreground
                           text-[10px] font-semibold flex items-center justify-center"
                >
                    {chip.number}
                </span>
                {chip.label}
            </span>
        {/each}
    </div>
</figure>
