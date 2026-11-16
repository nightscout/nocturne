/**
 * Finding signals by what the viewer types: every word typed must be in the signal's name or in its other words
 * (description, section, group, unit, abbreviations). Names that start with the text come first, then names that
 * hold it, then the rest, each in catalogue order. Case and accents are ignored.
 */
import type { SignalId } from "./catalogue-vocabulary.js";

/** The words a signal is found by. */
export interface SignalWords {
  name: string;
  other: readonly string[];
}

function fold(text: string): string {
  return text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().trim();
}

/** The signals that match `query`, best first; every signal, in order, for an empty query. */
export function searchSignals(query: string, ids: readonly SignalId[], wordsOf: (id: SignalId) => SignalWords): SignalId[] {
  const text = fold(query).replace(/\s+/g, " ");
  if (!text) return [...ids];
  const terms = text.split(" ");
  const ranked: { id: SignalId; rank: number; at: number }[] = [];
  ids.forEach((id, at) => {
    const words = wordsOf(id);
    const name = fold(words.name);
    const all = [name, ...words.other.map(fold)].join(" ");
    if (!terms.every((t) => all.includes(t))) return;
    const place = name.indexOf(text);
    const rank = place === 0 ? 0 : place > 0 ? 1 : 2;
    ranked.push({ id, rank, at });
  });
  return ranked.sort((a, b) => a.rank - b.rank || a.at - b.at).map((r) => r.id);
}
