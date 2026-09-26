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
  prerequisite?: string;
  completesKeys?: string[];
}

export interface SequenceConfig {
  [name: string]: SequenceDefinition;
}

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
}

export interface CoachMarkProviderOptions {
  adapter: CoachMarkAdapter;
  sequences?: SequenceConfig;
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
