<script lang="ts">
  import { page } from "$app/state";
  import { replaceState } from "$app/navigation";
  import { getCoachMarkContext } from "@nocturne/coach";
  import { beforeNavigate } from "$app/navigation";

  const ctx = getCoachMarkContext();

  // On navigation, clear quiet mode so organic sequences can fire on new pages
  beforeNavigate(() => {
    ctx.clearQuiet();
  });

  // React to coach param in URL
  $effect(() => {
    const coachParam = page.url.searchParams.get("coach");
    if (coachParam) {
      // Strip the param immediately
      const url = new URL(page.url);
      url.searchParams.delete("coach");
      replaceState(url, {});

      // Start the sequence (slight delay to let page elements mount)
      setTimeout(() => {
        ctx.startSequence(coachParam);
      }, 100);
    }
  });
</script>
