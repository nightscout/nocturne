<script lang="ts">
  import { page } from "$app/state";
  import AppBuildsPage from "./app-builds-page.svelte";
</script>

<AppBuildsPage
  linked={page.url.searchParams.get("linked")}
  error={page.url.searchParams.get("error")}
/>
