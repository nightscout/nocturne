<script lang="ts">
  import RecoveryCodes from "./RecoveryCodes.svelte";

  let { codes, onContinue }: { codes: string[]; onContinue: () => void } = $props();
</script>

<div class="space-y-3" data-testid="first-recovery-codes">
  <p class="text-sm text-muted-foreground">
    You are signed in. Your account had no recovery codes, so we made them now.
  </p>
  <RecoveryCodes {codes} {onContinue} />
</div>
