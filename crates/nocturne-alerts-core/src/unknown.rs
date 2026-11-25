//! Why a root is unknown (engine-semantics.md §2.4): the context facts whose
//! unknown reached it through the Kleene connectives, each with its reason.

use serde_json::{Map, Value};

use crate::clock_anomaly::ClockAnchor;
use crate::enums::{WireEnum, wire_enum};
use crate::facts::ContextFact;

wire_enum! {
    /// Why a leaf could not read a fact. `inexact` is a lower-bound reading
    /// that does not decide the comparison; `sensor_not_ok` is a glucose leaf
    /// whose sensor reports a state other than ok.
    #[derive(PartialOrd, Ord)]
    pub enum UnknownReason {
        Absent = "absent",
        Stale = "stale",
        AheadOfClock = "ahead_of_clock",
        Unrecognised = "unrecognised",
        Inexact = "inexact",
        SensorNotOk = "sensor_not_ok",
    }
}

/// A set of (fact, reason) pairs, one bit each.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct UnknownFacts(u128);

const _: () = assert!(ContextFact::ALL.len() * UnknownReason::ALL.len() <= 128);

fn index<T: WireEnum>(value: T) -> usize {
    T::ALL.iter().position(|&v| v == value).unwrap_or(0)
}

impl UnknownFacts {
    #[must_use]
    pub fn of(fact: ContextFact, reason: UnknownReason) -> Self {
        let bit = index(fact)
            .checked_mul(UnknownReason::ALL.len())
            .and_then(|b| b.checked_add(index(reason)))
            .and_then(|b| u32::try_from(b).ok())
            .and_then(|b| 1u128.checked_shl(b));
        UnknownFacts(bit.unwrap_or(0))
    }

    /// `fact` for the anchor an elapsed time beyond the tolerance ahead was
    /// measured from; nothing for an instant that is the engine's own state.
    #[must_use]
    pub fn ahead_of_clock(anchor: ClockAnchor) -> Self {
        anchor.fact().map_or_else(Self::default, |fact| {
            Self::of(fact, UnknownReason::AheadOfClock)
        })
    }

    #[must_use]
    pub fn union(self, other: Self) -> Self {
        UnknownFacts(self.0 | other.0)
    }

    #[must_use]
    pub fn is_empty(self) -> bool {
        self.0 == 0
    }

    /// In [`ContextFact::ALL`] order, then [`UnknownReason::ALL`] order.
    pub fn iter(self) -> impl Iterator<Item = (ContextFact, UnknownReason)> {
        ContextFact::ALL.iter().flat_map(move |&fact| {
            UnknownReason::ALL
                .iter()
                .filter(move |&&reason| self.0 & Self::of(fact, reason).0 != 0)
                .map(move |&reason| (fact, reason))
        })
    }

    /// `[{"fact": <fact>, "reason": <reason>}]`.
    #[must_use]
    pub fn to_json(self) -> Value {
        self.iter()
            .map(|(fact, reason)| {
                let mut o = Map::new();
                o.insert("fact".into(), fact.name().into());
                o.insert("reason".into(), reason.name().into());
                Value::Object(o)
            })
            .collect()
    }
}

impl FromIterator<(ContextFact, UnknownReason)> for UnknownFacts {
    fn from_iter<I: IntoIterator<Item = (ContextFact, UnknownReason)>>(iter: I) -> Self {
        iter.into_iter()
            .fold(Self::default(), |set, (f, r)| set.union(Self::of(f, r)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_pair_has_its_own_bit_and_reads_back() {
        let all: Vec<_> = ContextFact::ALL
            .iter()
            .flat_map(|&f| UnknownReason::ALL.iter().map(move |&r| (f, r)))
            .collect();
        let set: UnknownFacts = all.iter().copied().collect();
        assert_eq!(set.iter().collect::<Vec<_>>(), all);
        for &(f, r) in &all {
            assert_eq!(UnknownFacts::of(f, r).iter().collect::<Vec<_>>(), [(f, r)]);
        }
    }
}
