import type { LensId, SignalId, Vocabulary } from "./catalogue-vocabulary.js";
import type { ChartBlock, GivenBlock, Report } from "./report.svelte.js";
import { accessReason } from "./vocabulary-words.svelte.js";
import { unitLabel, type CatalogueEntry, type Reason, type RangeNeed } from "./workspace.svelte.js";

/**
 * What the signal catalogue lists and how it changes the chart it was opened from: a chart of signals
 * (every signal, with other views offered) or a chart held as a spec gave it (only the signals along
 * its axis).
 */
export interface CatalogueSource {
  vocab: Vocabulary;
  /** The lens the chart draws its signals under. */
  readonly lens: LensId;
  /** The chart's range, which the availability of each signal is read over. */
  readonly rangeHours: number;
  readonly glucoseUnit: string;
  readonly entries: CatalogueEntry[];
  /** The signals on the chart, in its order. */
  readonly onChart: SignalId[];
  readonly rangeNeeds: RangeNeed[];
  /** Whether the list holds every signal (true) or only the ones the chart's axis can take. */
  readonly everyView: boolean;
  /** Places the signal, or takes it off when it is on the chart. */
  toggle(id: SignalId): Reason;
  placeIn(id: SignalId, lens: LensId): Reason;
  placeInRange(id: SignalId, lens: LensId): number | null;
  useRange(hours: number): Reason;
}

/** The catalogue of a chart of signals: its workspace's. */
export function chartSource(report: Report, block: ChartBlock): CatalogueSource {
  const ws = block.ws;
  return {
    vocab: ws.vocab,
    get lens() {
      return ws.lens;
    },
    get rangeHours() {
      return ws.rangeHours;
    },
    get glucoseUnit() {
      return ws.glucoseUnit;
    },
    get entries() {
      return ws.catalogueEntries;
    },
    get onChart() {
      return ws.lens === "against" ? ws.againstPair : ws.shown;
    },
    get rangeNeeds() {
      return ws.rangeNeeds;
    },
    everyView: true,
    toggle: (id) => ws.toggleSignal(id),
    placeIn: (id, lens) => ws.placeIn(id, lens),
    placeInRange: (id, lens) => ws.placeInRange(id, lens),
    useRange: (hours) => report.setBlockRange(block, hours),
  };
}

/**
 * The catalogue of a chart held as a spec gave it: the signals with a cell under the lens of its axis.
 * Adding one adds a row to the chart; a signal is taken off from the chart's own list of layers, so
 * none counts as placed here.
 */
export function givenSource(report: Report, block: GivenBlock): CatalogueSource {
  const vocab = report.vocab;
  const unit = report.catalogue.datasets
    .find((d) => d.name === vocab.eventDataset)
    ?.fields.find((f) => f.name === "glucoseP50")?.unit;
  return {
    vocab,
    get lens() {
      return report.givenLens(block);
    },
    get rangeHours() {
      return report.hoursOf(block);
    },
    glucoseUnit: unitLabel(unit === "mmol/L" ? "mmol" : "mg/dl"),
    get entries() {
      const lens = report.givenLens(block);
      const most = report.catalogue.maxRangeHours;
      return vocab.signals.flatMap((s): CatalogueEntry[] => {
        const cell = vocab.cellOf(lens, s.id);
        if (!cell) return [];
        return [
          {
            id: s.id,
            placed: false,
            drawable: cell.permitted,
            noAccess: !cell.permitted,
            accessReason: cell.permitted ? null : accessReason(cell.scopes),
            upTo: cell.maxRangeHours < most ? cell.maxRangeHours : null,
            needsShorter: false,
            reason: cell.permitted ? report.givenSignalReason(block, s.id) : null,
            otherLenses: [],
          },
        ];
      });
    },
    onChart: [],
    rangeNeeds: [],
    everyView: false,
    toggle: (id) => report.addGivenSignal(block, id),
    placeIn: () => "This chart cannot show it.",
    placeInRange: () => null,
    useRange: (hours) => report.setBlockRange(block, hours),
  };
}
