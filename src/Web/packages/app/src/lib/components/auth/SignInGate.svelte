<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import LockKeyhole from "@lucide/svelte/icons/lock-keyhole";
  import { confirmIdentity } from "./confirm-identity.svelte";

  let {
    reason,
    onrequest,
  }: {
    /** One line that says what the person can do after they sign in. */
    reason: string;
    /** Runs before the prompt opens, for a gate inside a dialog that must close first. */
    onrequest?: () => void;
  } = $props();

  function signIn() {
    onrequest?.();
    void confirmIdentity.request();
  }
</script>

<Card.Root size="sm" data-testid="sign-in-gate">
  <div class="flex flex-wrap items-center gap-3">
    <LockKeyhole class="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
    <p class="min-w-0 flex-1 text-sm">{reason}</p>
    <Button size="sm" onclick={signIn}>Sign in</Button>
  </div>
</Card.Root>
