import { untrack } from "svelte";
import type { CoachNavigation, CoachRouter } from "./types.js";

export type SentinelWindow = Pick<
  Window,
  "history" | "addEventListener" | "removeEventListener" | "setTimeout"
>;

/**
 * Holds one shallow history entry above the page while an overlay is up, so the back button
 * dismisses the overlay rather than leaving the page.
 *
 * The entry is marked in the router's page state, which the router updates in its own popstate
 * listener. That listener can run after this one, so a pop is read a task later.
 *
 * SvelteKit's popstate handler resets its navigation token before it checks whether the entry
 * changed, so a `history.back()` issued while a navigation is loading cancels that navigation. The
 * entry is therefore never pushed or popped under a navigation in flight; the sentinel catches up
 * once it settles. An in-app link followed while the entry is current is cancelled instead, the
 * entry popped, and the link re-issued through the router, so the entry is not left stranded under
 * the new page as a back press that goes nowhere.
 */
export class HistorySentinel {
  private pushed = false;
  private wanted = false;
  private navigations = 0;
  private popping: Promise<void> | null = null;
  private settlePop: (() => void) | null = null;
  private router: CoachRouter | null = null;

  constructor(
    private readonly onBack: () => void,
    private readonly target?: SentinelWindow,
  ) {}

  private get win(): SentinelWindow {
    return this.target ?? window;
  }

  /** Must be called during component init, where the router accepts navigation hooks. */
  bindRouter(router: CoachRouter): void {
    this.router = router;
    router.beforeNavigate((navigation) => this.onBeforeNavigate(navigation));
  }

  connect(): () => void {
    const win = this.win;
    const onPopState = () => {
      win.setTimeout(() => {
        if (this.settlePop) {
          const settle = this.settlePop;
          this.settlePop = null;
          settle();
          return;
        }
        if (this.pushed && !this.router?.isMarkedEntry()) {
          this.pushed = false;
          this.onBack();
        }
      }, 0);
    };
    win.addEventListener("popstate", onPopState);
    return () => win.removeEventListener("popstate", onPopState);
  }

  push(): void {
    this.wanted = true;
    this.sync();
  }

  release(): void {
    this.wanted = false;
    this.sync();
  }

  /**
   * Callers push and release from effects. The router's page state is reactive, so reading it
   * tracked would re-run the caller's effect on every pop, and its teardown would pop a second time.
   */
  private sync(): void {
    untrack(() => {
      const router = this.router;
      if (!router || this.popping || this.navigations > 0) return;
      const marked = router.isMarkedEntry();
      if (this.wanted) {
        if (!marked) router.pushMarkedEntry();
        this.pushed = true;
      } else if (this.pushed) {
        this.pushed = false;
        if (marked) void this.pop();
      }
    });
  }

  private pop(): Promise<void> {
    if (this.popping) return this.popping;
    this.popping = new Promise<void>((resolve) => {
      this.settlePop = () => {
        this.popping = null;
        resolve();
        this.sync();
      };
      this.win.history.back();
    });
    return this.popping;
  }

  private settleAfter(done: Promise<unknown>): void {
    this.navigations++;
    const settle = () => {
      this.navigations--;
      this.sync();
    };
    done.then(settle, settle);
  }

  private onBeforeNavigate(navigation: CoachNavigation): void {
    const router = this.router;
    const to = navigation.to;
    if (
      router &&
      to &&
      navigation.type === "link" &&
      !navigation.willUnload &&
      this.pushed &&
      router.isMarkedEntry()
    ) {
      navigation.cancel();
      navigation.complete.catch(() => {});
      this.pushed = false;
      this.settleAfter(this.pop().then(() => router.goto(to.url)));
      return;
    }

    this.settleAfter(navigation.complete);
  }
}
