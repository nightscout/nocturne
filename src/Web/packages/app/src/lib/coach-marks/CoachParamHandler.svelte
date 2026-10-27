<script lang="ts">
  import { page } from "$app/state";
  import { replaceState } from "$app/navigation";
  import { getCoachMarkContext } from "@nocturne/coach";
  import { beforeNavigate } from "$app/navigation";
  import { withoutQueryParam } from "#lib/navigation/query-param.js";

  const ctx = getCoachMarkContext();

  // On navigation, clear quiet mode so organic sequences can fire on new pages
  beforeNavigate(() => {
    ctx.clearQuiet();
  });

  // React to coach param in URL
  $effect(() => {
    const coachParam = page.url.searchParams.get("coach");
    const url = withoutQueryParam(page.url, "coach");
    if (coachParam && url) {
      // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL minus one param, already resolved
      replaceState(url, {});

      // Start the sequence (slight delay to let page elements mount)
      setTimeout(() => {
        ctx.startSequence(coachParam);
      }, 100);
    }
  });
</script>
