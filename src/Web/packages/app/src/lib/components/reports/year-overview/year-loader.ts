import type {
  DailySummaryResponse,
  GriTimelineResponse,
  YearSummaryResponse,
} from "$api/generated/nocturne-api-client";

type Requests = {
  summary: (year: number, sources: string[]) => Promise<YearSummaryResponse>;
};

type Events = {
  daily: (year: number, result: DailySummaryResponse) => void;
  gri: (year: number, result: GriTimelineResponse) => void;
  loading: (year: number, loading: boolean) => void;
  error: (year: number, kind: "daily" | "gri", error: unknown) => void;
};

export class YearLoader {
  #generation = 0;
  #sources: string[] = [];
  #pending = new Map<number, Promise<boolean>>();
  #completed = new Map<number, boolean>();
  #tail: Promise<unknown> = Promise.resolve();
  #disposed = false;

  constructor(
    private requests: Requests,
    private events: Events
  ) {}

  reset(sources: string[]): void {
    this.#generation++;
    this.#sources = [...sources];
    this.#pending.clear();
    this.#completed.clear();
  }

  get busy(): boolean {
    return this.#pending.size > 0;
  }

  canLoad(year: number): boolean {
    return (
      !this.#disposed && !this.#pending.has(year) && !this.#completed.has(year)
    );
  }

  async retry(year: number): Promise<boolean> {
    const generation = this.#generation;
    await this.#pending.get(year);
    if (this.#disposed || generation !== this.#generation) return false;
    this.#completed.delete(year);
    return this.load(year);
  }

  load(year: number): Promise<boolean> {
    if (this.#disposed) return Promise.resolve(false);
    const pending = this.#pending.get(year);
    if (pending) return pending;
    const completed = this.#completed.get(year);
    if (completed !== undefined) return Promise.resolve(completed);

    const generation = this.#generation;
    const sources = [...this.#sources];
    const current = () => !this.#disposed && generation === this.#generation;
    this.events.loading(year, true);

    // Superseded remote queries cannot be aborted; keep their place in the
    // queue so rapid filter changes cannot start unbounded database work.
    const run = this.#tail
      .then(async () => {
        if (!current()) return false;
        try {
          const result = await this.requests.summary(year, sources);
          if (!current()) return false;
          if (result.dailySummary) this.events.daily(year, result.dailySummary);
          else this.events.error(year, "daily", null);
          if (result.griTimeline) this.events.gri(year, result.griTimeline);
          else this.events.error(year, "gri", null);
          return Boolean(result.dailySummary && result.griTimeline);
        } catch (error) {
          if (current()) {
            this.events.error(year, "daily", error);
            this.events.error(year, "gri", error);
          }
          return false;
        }
      })
      .finally(() => {
        if (current()) {
          this.#pending.delete(year);
          this.events.loading(year, false);
        }
      });
    this.#pending.set(year, run);
    this.#tail = run.catch(() => {});
    void run.then((success) => {
      if (current()) this.#completed.set(year, success);
    });
    return run;
  }

  dispose(): void {
    this.#disposed = true;
    this.#generation++;
    this.#pending.clear();
  }
}
