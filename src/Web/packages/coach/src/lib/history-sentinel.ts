import type { CoachNavigation, CoachRouter } from "./types.js";

const MARKER = "__coachMark";

export type SentinelWindow = Pick<
  Window,
  "history" | "addEventListener" | "removeEventListener" | "setTimeout"
>;

/**
 * Holds one history entry above the page while an overlay is up, so the back button dismisses the
 * overlay rather than leaving the page.
 *
 * SvelteKit's popstate handler resets its navigation token before it checks whether the entry
 * changed, so a `history.back()` issued while a navigation is loading cancels that navigation. The
 * entry is therefore never popped under a navigation in flight. An in-app link followed while the
 * entry is current is cancelled instead, the entry popped, and the link re-issued through the
 * router, so the entry is not left stranded under the new page as a back press that goes nowhere.
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
      if (this.settlePop) {
        const settle = this.settlePop;
        this.settlePop = null;
        // Every other popstate listener, the router's included, has to finish with this entry
        // before anything navigates again.
        win.setTimeout(settle, 0);
        return;
      }
      if (this.pushed && !this.isMarked()) {
        this.pushed = false;
        this.onBack();
      }
    };
    win.addEventListener("popstate", onPopState);

    // A reload while an overlay was up leaves its entry current.
    if (this.isMarked()) {
      this.pushed = true;
      void this.pop();
    }

    return () => win.removeEventListener("popstate", onPopState);
  }

  push(): void {
    this.wanted = true;
    if (this.pushed || this.popping || this.navigations > 0) return;
    const { history } = this.win;
    history.pushState({ ...history.state, [MARKER]: true }, "");
    this.pushed = true;
  }

  release(): void {
    this.wanted = false;
    if (this.navigations > 0) {
      this.pushed = false;
      return;
    }
    void this.pop();
  }

  private isMarked(): boolean {
    return this.win.history.state?.[MARKER] === true;
  }

  private pop(): Promise<void> {
    if (this.popping) return this.popping;
    if (!this.pushed || !this.isMarked()) {
      this.pushed = false;
      return Promise.resolve();
    }

    this.pushed = false;
    this.popping = new Promise<void>((resolve) => {
      this.settlePop = () => {
        this.popping = null;
        resolve();
        if (this.wanted) this.push();
      };
      this.win.history.back();
    });
    return this.popping;
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
      this.isMarked()
    ) {
      navigation.cancel();
      navigation.complete.catch(() => {});
      this.wanted = false;
      void this.pop().then(() => router.goto(to.url));
      return;
    }

    this.navigations++;
    const settle = () => {
      this.navigations--;
    };
    navigation.complete.then(settle, settle);
  }
}
