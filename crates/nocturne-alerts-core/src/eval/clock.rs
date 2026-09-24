//! Wall-clock leaves: time_of_day, day_of_week, time_since_last_carb/bolus.

use chrono::{DateTime, Datelike, NaiveDateTime, NaiveTime, Utc};
use chrono_tz::Tz;

use super::Env;
use crate::compare::{Unit, elapsed};
use crate::enums::{DayOfWeek, EnumValue, WireEnum};
use crate::model::{DayOfWeekPayload, TimeOfDayPayload, TimeSincePayload};

/// `HH:mm` exactly: two digits each, 00-23 and 00-59.
fn parse_hh_mm(s: &str) -> Option<NaiveTime> {
    let two_digits = |t: &str| {
        (t.len() == 2 && t.bytes().all(|b| b.is_ascii_digit()))
            .then(|| t.parse().ok())
            .flatten()
    };
    let (hh, mm) = s.split_once(':')?;
    NaiveTime::from_hms_opt(two_digits(hh)?, two_digits(mm)?, 0)
}

/// An IANA zone id, matched exactly and then ignoring ASCII case, since
/// stored ids are often mis-cased (`ETC/GMT-2`). Windows ids do not resolve,
/// and tzdb backward links (`US/Pacific`) do (engine-semantics.md §4).
fn find_tz(id: &str) -> Option<Tz> {
    id.parse().ok().or_else(|| {
        chrono_tz::TZ_VARIANTS
            .iter()
            .find(|tz| tz.name().eq_ignore_ascii_case(id))
            .copied()
    })
}

/// `now` as wall time in the tenant's zone, or UTC when it has none or it
/// does not resolve.
fn tenant_local(env: &Env) -> NaiveDateTime {
    local(
        env.now,
        env.ctx.tenant_time_zone_id.as_deref().and_then(zone),
    )
}

/// A non-empty zone id that resolves.
fn zone(id: &str) -> Option<Tz> {
    Some(id).filter(|id| !id.is_empty()).and_then(find_tz)
}

fn local(now: DateTime<Utc>, tz: Option<Tz>) -> NaiveDateTime {
    tz.map_or_else(
        || now.naive_utc(),
        |tz| now.with_timezone(&tz).naive_local(),
    )
}

/// Half-open `[from, to)` window; `from > to` wraps midnight. A per-rule
/// timezone wins and fails closed when it does not resolve; without one the
/// window is in the tenant's zone.
pub(super) fn time_of_day(p: &TimeOfDayPayload, env: &Env) -> bool {
    let (Some(from), Some(to)) = (
        p.from.as_deref().and_then(parse_hh_mm),
        p.to.as_deref().and_then(parse_hh_mm),
    ) else {
        return false;
    };
    let local = match p.timezone.as_deref().filter(|id| !id.is_empty()) {
        Some(id) => match find_tz(id) {
            Some(tz) => local(env.now, Some(tz)),
            None => return false,
        },
        None => tenant_local(env),
    };
    let current = local.time();
    if from <= to {
        current >= from && current < to
    } else {
        current >= from || current < to
    }
}

/// Today, in the tenant's zone, is one of `days`; an undefined day never
/// matches.
pub(super) fn day_of_week(p: &DayOfWeekPayload, env: &Env) -> bool {
    let today = tenant_local(env).weekday().num_days_from_sunday();
    DayOfWeek::from_ordinal(i64::from(today)).is_some_and(|today| {
        p.days
            .iter()
            .flatten()
            .any(|d| *d == EnumValue::Known(today))
    })
}

/// Elapsed minutes since `anchor` as a double; a missing anchor is +infinity,
/// so `>` and `>=` hold on cold start, unlike loop_stale.
pub(super) fn time_since(p: &TimeSincePayload, anchor: Option<DateTime<Utc>>, env: &Env) -> bool {
    let since = match anchor {
        Some(anchor) => elapsed(env.now, anchor, Unit::Minutes),
        None => Some(f64::INFINITY),
    };
    p.operator
        .known()
        .zip(since)
        .is_some_and(|(op, since)| op.apply(since, f64::from(p.minutes)))
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

    /// The other half of the documented divergence: `TZ_VARIANTS` carries tzdb backward
    /// links that C#'s ICU-canonical scan rejects, so these resolve here and fail closed
    /// under the managed engine. Pinned so a change to the set is a deliberate one.
    #[test]
    fn resolves_backward_links_that_the_managed_engine_rejects() {
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
