//! A scene's paint-on sequence in simulation ticks.
//!
//! One tick is one fixed simulation step of [`super::sim::DT`]. The timeline
//! knows nothing about wall-clock or artistic duration; that mapping is
//! `application::playback`.

use super::ops::Operation;

#[derive(Debug, Clone, PartialEq)]
pub struct TimelineEvent {
    pub at_tick: u32,
    pub op: Operation,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Timeline {
    /// Kept sorted by `at_tick`; events on the same tick apply in order.
    pub events: Vec<TimelineEvent>,
    pub total_ticks: u32,
}

impl Timeline {
    pub fn new(total_ticks: u32) -> Timeline {
        Timeline {
            events: Vec::new(),
            total_ticks,
        }
    }

    pub fn push(&mut self, at_tick: u32, op: Operation) {
        let idx = self.events.partition_point(|e| e.at_tick <= at_tick);
        self.events.insert(idx, TimelineEvent { at_tick, op });
    }

    /// Events scheduled exactly at `tick`, with their indices.
    pub fn events_at(&self, tick: u32) -> impl Iterator<Item = (usize, &TimelineEvent)> {
        self.events
            .iter()
            .enumerate()
            .filter(move |(_, e)| e.at_tick == tick)
    }

    /// Distinct ticks that carry at least one event, ascending.
    pub fn event_ticks(&self) -> Vec<u32> {
        let mut ticks: Vec<u32> = self.events.iter().map(|e| e.at_tick).collect();
        ticks.dedup();
        ticks
    }

    pub fn is_empty(&self) -> bool {
        self.events.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_keeps_events_sorted_and_stable() {
        let mut t = Timeline::new(100);
        t.push(50, Operation::DryAll);
        t.push(10, Operation::Dry { rate: 1.0 });
        t.push(50, Operation::ClearMask);
        let ticks: Vec<u32> = t.events.iter().map(|e| e.at_tick).collect();
        assert_eq!(ticks, vec![10, 50, 50]);
        assert_eq!(t.events[1].op, Operation::DryAll);
        assert_eq!(t.events[2].op, Operation::ClearMask);
        assert_eq!(t.event_ticks(), vec![10, 50]);
    }
}
