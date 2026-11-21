//! Whether an evaluation stays quiet until the `next_change_at` it reported
//! (engine-semantics.md §5.3), shared by the example and property suites.

use chrono::{DateTime, TimeDelta, Utc};

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Evaluation, Rule, evaluate_rule};
use nocturne_alerts_core::excursion::TransitionType;

#[derive(Debug, PartialEq)]
struct Outcome {
    root: bool,
    leaves: Option<Vec<bool>>,
    transition: TransitionType,
    auto_resolved: bool,
}

impl Outcome {
    fn of(e: &Evaluation) -> Self {
        Outcome {
            root: e.root,
            leaves: e.leaves.clone(),
            transition: e.transition.kind,
            auto_resolved: e.auto_resolved,
        }
    }
}

pub(crate) fn evaluate(
    rule: &Rule,
    ctx: &SensorContext,
    state: &mut EngineState,
    t: DateTime<Utc>,
) -> Evaluation {
    evaluate_rule(rule, ctx, t, state, true)
        .evaluation
        .expect("rule evaluates")
}

/// Ticks in `(now, end)`: the first two, the last two, and ticks spread
/// between.
pub(crate) fn ticks_inside(now: DateTime<Utc>, end: DateTime<Utc>) -> Vec<DateTime<Utc>> {
    let ticks = (end - now).num_nanoseconds().unwrap_or(i64::MAX) / 100;
    let mut points: Vec<DateTime<Utc>> = [1, 2, ticks - 1, ticks - 2]
        .into_iter()
        .chain((1..16).map(|i| ticks / 16 * i))
        .map(|k| now + TimeDelta::nanoseconds(k * 100))
        .collect();
    points.retain(|&t| t > now && t < end);
    points
}

/// Whether an evaluation at `t`, from the state the evaluation at `now` left,
/// repeats that evaluation's root and leaves with no transition other than
/// `continues` and no auto-resolve. `before` is the state the evaluation at
/// `now` starts from, made afresh on each call.
pub(crate) fn quiet_at(
    rule: &Rule,
    ctx: &SensorContext,
    before: impl Fn() -> EngineState,
    now: DateTime<Utc>,
    t: DateTime<Utc>,
) -> Result<(), String> {
    let mut state = before();
    let first = Outcome::of(&evaluate(rule, ctx, &mut state, now));
    let mut then = Outcome::of(&evaluate(rule, ctx, &mut state, t));
    if then.transition == TransitionType::ExcursionContinues {
        then.transition = TransitionType::None;
    }
    let quiet = Outcome {
        transition: TransitionType::None,
        auto_resolved: false,
        ..first
    };
    if then == quiet {
        Ok(())
    } else {
        Err(format!("at {t}: {then:?}, quiet is {quiet:?}"))
    }
}

/// The property: with `N` the `next_change_at` of the evaluation at `now`
/// (or `now + 3 days` when it has none), `N > now`, and at every sampled tick
/// `t` in `(now, N)` the rule is quiet ([`quiet_at`]).
pub(crate) fn quiet_until_next_change(
    rule: &Rule,
    ctx: &SensorContext,
    before: impl Fn() -> EngineState,
    now: DateTime<Utc>,
) -> Result<(), String> {
    let next = evaluate(rule, ctx, &mut before(), now).next_change_at;
    if let Some(next) = next
        && next <= now
    {
        return Err(format!("{next} is not after {now}"));
    }
    let end = next.unwrap_or(now + TimeDelta::days(3));
    for t in ticks_inside(now, end) {
        quiet_at(rule, ctx, &before, now, t).map_err(|e| format!("before {end}, {e}"))?;
    }
    Ok(())
}
