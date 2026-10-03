<script lang="ts">
  import { setPreferencesContext, type RequestPreferences } from "../stores/appearance-store.svelte";
  import { a1cLabel, formatA1c } from "../utils/a1c-formatting";

  let { layers }: RequestPreferences = $props();
  setPreferencesContext(() => ({ layers }));
</script>

<span>{a1cLabel()} / {a1cLabel(true)}: {formatA1c({ percent: 7, mmolMol: 53 })}</span>
