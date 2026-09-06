//! Wall-clock leaves: time_of_day, day_of_week, time_since_last_carb/bolus.

use chrono::{DateTime, Datelike, NaiveTime, Utc};
use chrono_tz::Tz;

use super::Env;
use crate::compare::total_minutes;
use crate::model::{DayOfWeekPayload, TimeOfDayPayload, TimeSincePayload};

/// `TimeOnly.TryParseExact(s, "HH:mm")`: exactly two digits each, 00-23 /
/// 00-59, nothing else.
fn parse_hh_mm(s: &str) -> Option<NaiveTime> {
    let b = s.as_bytes();
    if b.len() != 5 || b[2] != b':' {
        return None;
    }
    let digit = |c: u8| -> Option<u32> { (c as char).to_digit(10) };
    let hh = digit(b[0])? * 10 + digit(b[1])?;
    let mm = digit(b[3])? * 10 + digit(b[4])?;
    NaiveTime::from_hms_opt(hh, mm, 0)
}

/// Resolves an IANA timezone id, mirroring the resolution order of the C#
/// `TimeZoneHelper`: exact match first, then a case-insensitive match against
/// the zone table.
///
/// The case-insensitive pass is not cosmetic. `chrono_tz`'s `FromStr` is an
/// exact-match table lookup, while `TimeZoneHelper` deliberately resolves
/// mis-cased ids because connector data carries them in bulk (production has
/// ~240 rows spelling `Etc/GMT-2` as `ETC/GMT-2`). Without this the two engines
/// disagree on every such rule: a mis-cased *per-rule* tz fails closed, so an
/// overnight low rule never fires, and a mis-cased *tenant* tz silently shifts
/// the whole rule set to UTC.
///
/// The two retries agree on that population but are not the same set. Windows
/// ids (`AUS Eastern Standard Time`) resolve in C# and not here; conversely
/// `TZ_VARIANTS` includes tzdb backward links (`Etc/Greenwich`, `US/Pacific`)
/// that C#'s ICU-canonical scan rejects, so those resolve here and not there.
/// Both divergences are cutover gates — see `docs/alerts/engine-semantics.md` §4.
fn find_tz(id: &str) -> Option<Tz> {
    if let Ok(tz) = id.parse::<Tz>() {
        return Some(tz);
    }
    chrono_tz::TZ_VARIANTS
        .iter()
        .find(|tz| tz.name().eq_ignore_ascii_case(id))
        .copied()
}

fn local_now(now: DateTime<Utc>, tz: Option<Tz>) -> chrono::NaiveDateTime {
    match tz {
        Some(tz) => now.with_timezone(&tz).naive_local(),
        None => now.naive_utc(),
    }
}

/// Half-open `[from, to)` window in the resolved timezone; `from > to` wraps
/// midnight. Resolution: an explicit per-rule timezone wins and fails closed
/// when unknown; a missing per-rule tz falls back to the tenant tz (unknown
/// tenant tz swallows to UTC); both absent → UTC.
pub(super) fn time_of_day(p: &TimeOfDayPayload, env: &Env) -> bool {
    let (Some(from), Some(to)) = (
        p.from.as_deref().and_then(parse_hh_mm),
        p.to.as_deref().and_then(parse_hh_mm),
    ) else {
        return false;
    };

    let tz = match p.timezone.as_deref() {
        Some(tz_id) if !tz_id.is_empty() => match find_tz(tz_id) {
            Some(tz) => Some(tz),
            None => return false,
        },
        _ => match env.ctx.tenant_time_zone_id.as_deref() {
            Some(tenant_tz) if !tenant_tz.is_empty() => find_tz(tenant_tz),
            _ => None,
        },
    };

    let local = local_now(env.now, tz);
    // TimeOnly.FromDateTime keeps sub-minute precision; corpus instants are
    // whole seconds, so second precision suffices.
    let current = local.time();

    if from <= to {
        current >= from && current < to
    } else {
        current >= from || current < to
    }
}

/// Local `now.DayOfWeek ∈ days` in the tenant timezone (unknown/missing → UTC).
/// Day values follow `System.DayOfWeek`: 0 = Sunday … 6 = Saturday; integers
/// outside that range simply never match.
pub(super) fn day_of_week(p: &DayOfWeekPayload, env: &Env) -> bool {
    let Some(days) = &p.days else {
        return false;
    };
    if days.is_empty() {
        return false;
    }
    let tz = env
        .ctx
        .tenant_time_zone_id
        .as_deref()
        .filter(|id| !id.is_empty())
        .and_then(find_tz);
    let local = local_now(env.now, tz);
    let today = i64::from(local.weekday().num_days_from_sunday());
    days.contains(&today)
}

/// `TimeSinceComparator.Apply`: elapsed minutes in f64; a missing anchor is
/// +∞ (so `>`/`>=` fire on cold start — deliberately opposite to loop_stale).
/// Operator ordinals: 0 `>`, 1 `>=`, 2 `<`, 3 `<=`, 4 `==`; anything else
/// fails closed.
pub(super) fn time_since(p: &TimeSincePayload, anchor: Option<DateTime<Utc>>, env: &Env) -> bool {
    let elapsed = match anchor {
        Some(anchor) => total_minutes(env.now - anchor),
        None => f64::INFINITY,
    };
    let threshold = f64::from(p.minutes);
    match p.operator {
        0 => elapsed > threshold,
        1 => elapsed >= threshold,
        2 => elapsed < threshold,
        3 => elapsed <= threshold,
        4 => elapsed == threshold,
        _ => false,
    }
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
