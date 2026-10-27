<script lang="ts">
    import { syncedAsset } from "#lib/synced-asset.js";
    import videos from "@nocturne/video/manifest.json";
    import { track } from "#lib/analytics.js";

    interface Props {
        id: string;
        class?: string;
    }

    let { id, class: className = "" }: Props = $props();

    type Entry = (typeof videos)[keyof typeof videos];
    const entries: Record<string, Entry> = videos;

    // A stale id throws rather than degrading, so a docs page naming a video that was never
    // published fails the prerendered build instead of shipping a broken player.
    const entry = $derived.by(() => {
        const found = entries[id];
        if (!found) {
            throw new Error(
                `Video "${id}" is not in the @nocturne/video manifest. Render and publish it (pnpm --filter @nocturne/video run publish), or correct the id.`,
            );
        }
        return found;
    });

    // Once each per pageview: a replay or a scrub back to the start is not a second viewer.
    let played = false;
    let completed = false;

    function onplay() {
        if (played) return;
        played = true;
        track("Video Play", { video: id });
    }

    function onended() {
        if (completed) return;
        completed = true;
        track("Video Complete", { video: id });
    }
</script>

<figure class="not-prose my-6 {className}">
    <!-- The films carry no sound; their notes are on screen and in the transcript below. -->
    <video
        controls
        playsinline
        preload="none"
        poster={syncedAsset(`videos/${entry.poster}`)}
        width={entry.width}
        height={entry.height}
        aria-label={entry.title}
        class="w-full h-auto rounded-lg border border-border/60 bg-muted/30"
        {onplay}
        {onended}
    >
        <source src={syncedAsset(`videos/${entry.video}`)} type="video/mp4" />
    </video>
    <details class="mt-3 rounded-lg border border-border/60 p-4">
        <summary class="cursor-pointer font-semibold">Transcript</summary>
        <div class="mt-3 space-y-3 text-sm">
            <p>{entry.summary}</p>
            {#each entry.transcript as scene (scene.heading)}
                <div>
                    <p class="font-semibold">{scene.heading}</p>
                    <ul class="list-disc space-y-1 pl-5">
                        {#each scene.notes as note (note)}
                            <li>{note}</li>
                        {/each}
                    </ul>
                </div>
            {/each}
        </div>
    </details>
</figure>
