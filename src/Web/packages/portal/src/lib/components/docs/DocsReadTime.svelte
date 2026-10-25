<script lang="ts">
    import { onMount } from "svelte";
    import { afterNavigate } from "$app/navigation";
    import { READ_MILESTONES, track } from "$lib/analytics";

    // A second counts only while the tab is visible and the reader has scrolled, moved the
    // pointer, touched or typed within this window, so a page left open in the background or
    // on an idle screen is not reported as read.
    const IDLE_MS = 30_000;
    const TICK_MS = 1000;

    let engagedMs = 0;
    let next = 0;
    let lastActivity = 0;

    function tick() {
        if (document.visibilityState !== "visible") return;
        if (Date.now() - lastActivity > IDLE_MS) return;

        engagedMs += TICK_MS;
        while (next < READ_MILESTONES.length && engagedMs >= Number(READ_MILESTONES[next]) * 1000) {
            track("Docs Read", { seconds: READ_MILESTONES[next] });
            next++;
        }
    }

    function active() {
        lastActivity = Date.now();
    }

    onMount(() => {
        const events = ["scroll", "pointermove", "pointerdown", "keydown", "touchstart"] as const;
        for (const name of events) window.addEventListener(name, active, { passive: true });
        const timer = setInterval(tick, TICK_MS);
        return () => {
            clearInterval(timer);
            for (const name of events) window.removeEventListener(name, active);
        };
    });

    // Each pageview starts its own count; arriving on the page is activity.
    afterNavigate(() => {
        engagedMs = 0;
        next = 0;
        active();
    });
</script>
