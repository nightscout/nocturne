export type MarkStatus = "unseen" | "seen" | "dismissed" | "completed";

export interface DismissOptions {
  quiet?: boolean;
}

export interface MarkState {
  id: string;
  markKey: string;
  status: MarkStatus;
  seenAt: string | null;
  completedAt: string | null;
}

export interface CoachMarkAdapter {
  fetchAll: () => Promise<MarkState[]>;
  update: (key: string, status: MarkStatus) => Promise<void>;
  deleteAll?: () => Promise<void>;
}

export interface CoachMarkStep {
  title: string;
  description: string;
}

export interface CoachMarkOptions {
  key: string;
  step?: number;
  title?: string;
  description?: string;
  steps?: CoachMarkStep[];
  action?: { label: string; href: string };
  completedWhen?: () => boolean;
  completeOn?: {
    event: string;
    target?: HTMLElement | string;
  };
  priority?: number;
}

export interface SequenceDefinition {
  priority: number;
  steps: string[];
  /** A sequence that must be done first, or a {@link CoachGates} entry the host must set true. */
  prerequisite?: string;
  completesKeys?: string[];
  /** Dismissing a step of this sequence also holds back every other mark until the next navigation. */
  quietOnDismiss?: boolean;
  /**
   * Shown only by `startSequence`, never chosen by organic selection. A started run outlives
   * navigation and ends once its last step is done or one of its steps is dismissed. It shows its
   * first unfinished step that is mounted; while none is, organic marks are chosen as usual, and a
   * step that mounts with no mark up takes the floor. A reload or another started run drops it.
   */
  startOnly?: boolean;
}

export interface SequenceConfig {
  [name: string]: SequenceDefinition;
}

/** Conditions outside the coach marks, named by a sequence's `prerequisite`. */
export type CoachGates = Readonly<Record<string, boolean>>;

/** The part of a router's before-navigate event the provider needs; SvelteKit's `BeforeNavigate` fits. */
export interface CoachNavigation {
  type: string;
  to: { url: URL } | null;
  willUnload: boolean;
  cancel: () => void;
  complete: Promise<void>;
}

export interface CoachRouter {
  /** Registers a callback run before every client-side navigation. Called once, at provider init. */
  beforeNavigate(callback: (navigation: CoachNavigation) => void): void;
  goto(url: URL): Promise<unknown>;
  /** Pushes a shallow entry on the current URL whose page state carries the coach marker. */
  pushMarkedEntry(): void;
  /** Whether the current entry's page state carries the coach marker. */
  isMarkedEntry(): boolean;
}

/** The popover's control text. The app supplies it so the package carries no language of its own. */
export interface CoachLabels {
  dismiss: string;
  showTip: string;
  back: string;
  next: string;
  gotIt: string;
  stepCounter: (current: number, total: number) => string;
}

export interface CoachMarkProviderOptions {
  adapter: CoachMarkAdapter;
  labels: CoachLabels;
  sequences?: SequenceConfig;
  gates?: CoachGates;
  settleDelay?: number;
  seenDwellMs?: number;
}

export interface MarkRegistration {
  key: string;
  step: number;
  title: string;
  description: string;
  action?: { label: string; href: string };
  completedWhen?: () => boolean;
  completeOn?: {
    event: string;
    target?: HTMLElement | string;
  };
  priority: number;
  element: HTMLElement;
}
