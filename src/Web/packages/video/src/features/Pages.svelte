<script>
  import { pages } from "virtual:feature";
</script>

{#each pages as Page, i (i)}
  <section data-feature-page={i}>
    <Page />
  </section>
{/each}
