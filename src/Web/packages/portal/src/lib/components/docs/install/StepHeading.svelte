<script lang="ts">
    import type { Snippet } from "svelte";

    interface Props {
        /** The anchor the sticky bar links to. */
        id: string;
        /** The `data-install-step` value reported to analytics, where the step has one. */
        installStep?: string;
        children: Snippet;
    }

    let { id, installStep, children }: Props = $props();
</script>

<!-- scroll-mt clears the site header and the sticky guide bar. -->
<h2 {id} data-install-step={installStep} class="text-2xl font-bold mt-12 mb-4 scroll-mt-48 md:scroll-mt-36">
    {@render children()}
</h2>
