//! Wall-clock leaves: time_of_day, day_of_week, time_since_last_carb/bolus.

use chrono::{DateTime, Datelike, NaiveDateTime, Utc, Weekday};
use chrono_tz::Tz;

use super::Env;
use super::elapsed::{ClockLeaf, Elapsed, Measure};
use crate::context::SensorContext;
use crate::enums::DayOfWeek;
use crate::model::{DayOfWeekPayload, TimeOfDayPayload, TimeSincePayload};
use crate::truth::Truth;

/// The tenant's zone id: an IANA zone id, matched exactly and then ignoring
/// ASCII case, since stored tenant ids are often mis-cased (`ETC/GMT-2`).
/// Windows ids do not resolve, and tzdb backward links (`US/Pacific`) do
/// (engine-semantics.md §4).
fn find_tz(id: &str) -> Option<Tz> {
    id.parse().ok().or_else(|| {
        chrono_tz::TZ_VARIANTS
            .iter()
            .find(|tz| tz.name().eq_ignore_ascii_case(id))
            .copied()
    })
}

/// The tenant's zone, or `None` for UTC when it has none or it does not
/// resolve.
pub(crate) fn tenant_zone(ctx: &SensorContext) -> Option<Tz> {
    ctx.tenant_time_zone_id.as_deref().and_then(zone)
}

/// The zone a `time_of_day` window is read in: the per-rule timezone, else
/// the tenant's.
pub(crate) fn time_of_day_zone(p: &TimeOfDayPayload, ctx: &SensorContext) -> Option<Tz> {
    p.timezone.or_else(|| tenant_zone(ctx))
}

/// A non-empty zone id that resolves.
fn zone(id: &str) -> Option<Tz> {
    Some(id).filter(|id| !id.is_empty()).and_then(find_tz)
}

/// `now` as wall time in `tz`, UTC when `None`.
pub(crate) fn local(now: DateTime<Utc>, tz: Option<Tz>) -> NaiveDateTime {
    tz.map_or_else(
        || now.naive_utc(),
        |tz| now.with_timezone(&tz).naive_local(),
    )
}

/// Half-open `[from, to)` window; `from > to` wraps midnight.
pub(super) fn time_of_day(p: &TimeOfDayPayload, env: &Env) -> Truth {
    let (from, to) = (p.from, p.to);
    let current = local(env.now, time_of_day_zone(p, env.ctx)).time();
    if from <= to {
        (current >= from && current < to).into()
    } else {
        (current >= from || current < to).into()
    }
}

/// Today, in the tenant's zone, is one of `days`.
pub(super) fn day_of_week(p: &DayOfWeekPayload, env: &Env) -> Truth {
    let today = match local(env.now, tenant_zone(env.ctx)).weekday() {
        Weekday::Sun => DayOfWeek::Sunday,
        Weekday::Mon => DayOfWeek::Monday,
        Weekday::Tue => DayOfWeek::Tuesday,
        Weekday::Wed => DayOfWeek::Wednesday,
        Weekday::Thu => DayOfWeek::Thursday,
        Weekday::Fri => DayOfWeek::Friday,
        Weekday::Sat => DayOfWeek::Saturday,
    };
    p.days.contains(&today).into()
}

/// Elapsed minutes since `anchor` as a double; a missing anchor is +infinity,
/// so `>` and `>=` hold on cold start, unlike loop_stale.
pub(super) fn time_since(p: &TimeSincePayload, anchor: Option<DateTime<Utc>>) -> ClockLeaf {
    let minutes = f64::from(p.minutes.get());
    anchor.map_or(
        ClockLeaf::Fixed(p.operator.apply(f64::INFINITY, minutes).into()),
        |anchor| {
            ClockLeaf::Measured(Elapsed {
                anchor,
                measure: Measure::Minutes {
                    op: p.operator,
                    minutes,
                },
            })
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_exact_iana_ids() {
        assert_eq!(find_tz("Etc/GMT-2").map(Tz::name), Some("Etc/GMT-2"));
        assert_eq!(
            find_tz("Australia/Sydney").map(Tz::name),
            Some("Australia/Sydney")
        );
    }

    /// The production case: ~240 rows spell `Etc/GMT-2` as `ETC/GMT-2`.
    #[test]
    fn resolves_miscased_etc_ids() {
        assert_eq!(find_tz("ETC/GMT-2").map(Tz::name), Some("Etc/GMT-2"));
        assert_eq!(find_tz("etc/utc").map(Tz::name), Some("Etc/UTC"));
    }

    #[test]
    fn resolves_miscased_region_ids() {
        assert_eq!(
            find_tz("australia/sydney").map(Tz::name),
            Some("Australia/Sydney")
        );
        assert_eq!(
            find_tz("AMERICA/NEW_YORK").map(Tz::name),
            Some("America/New_York")
        );
    }

    #[test]
    fn rejects_unknown_ids() {
        assert!(find_tz("Not/AZone").is_none());
        assert!(find_tz("").is_none());
        // Windows ids stay unresolved here — documented divergence from TimeZoneHelper.
        assert!(find_tz("AUS Eastern Standard Time").is_none());
    }

    /// `TZ_VARIANTS` carries tzdb backward links (engine-semantics.md §4), so these
    /// resolve. Pinned so a change to the set is a deliberate one.
    #[test]
    fn resolves_backward_links() {
        assert_eq!(
            find_tz("Etc/Greenwich").map(Tz::name),
            Some("Etc/Greenwich")
        );
        assert_eq!(find_tz("US/Pacific").map(Tz::name), Some("US/Pacific"));
        assert_eq!(
            find_tz("Asia/Calcutta").map(Tz::name),
            Some("Asia/Calcutta")
        );
    }
}
