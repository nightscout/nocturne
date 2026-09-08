<script lang="ts">
    import { onMount } from "svelte";
    import { afterNavigate } from "$app/navigation";
    import { track } from "$lib/analytics";

    const MILESTONES = [25, 50, 75, 100] as const;

    // A page whose content is still arriving (fonts, images, the mdsvex body) is shorter than
    // it will be, and measuring it then would report depths the reader never reached. Nothing
    // is measured until the layout has had this long to settle.
    const SETTLE_MS = 1000;

    let reached = new Set<number>();
    let settled = false;
    let settleTimer: ReturnType<typeof setTimeout> | undefined;

    function report() {
        if (!settled) return;

        const doc = document.documentElement;
        const scrollable = doc.scrollHeight - window.innerHeight;
        // A page shorter than the viewport is entirely on screen, so it is read to the end.
        const percent =
            scrollable <= 0 ? 100 : Math.min(100, (window.scrollY / scrollable) * 100);

        for (const milestone of MILESTONES) {
            if (percent >= milestone && !reached.has(milestone)) {
                reached.add(milestone);
                track("Docs Scroll", { depth: String(milestone) });
            }
        }
    }

    function restart() {
        reached = new Set();
        settled = false;
        clearTimeout(settleTimer);
        settleTimer = setTimeout(() => {
            settled = true;
            report();
        }, SETTLE_MS);
    }

    onMount(() => {
        let queued = false;
        const onScroll = () => {
            if (queued) return;
            queued = true;
            requestAnimationFrame(() => {
                queued = false;
                report();
            });
        };

        window.addEventListener("scroll", onScroll, { passive: true });
        window.addEventListener("resize", onScroll, { passive: true });
        return () => {
            clearTimeout(settleTimer);
            window.removeEventListener("scroll", onScroll);
            window.removeEventListener("resize", onScroll);
        };
    });

    // Each docs page is its own set of milestones. The layout survives navigation between
    // sibling pages, so the tally has to be cleared here rather than on mount.
    afterNavigate(restart);
</script>
