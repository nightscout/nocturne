//! `ComparisonOps` and the elapsed-time numeric conventions.
//!
//! C# computes `TimeSpan.TotalMinutes/Hours/Days` as `ticks * (1.0 / TicksPerX)`
//! (multiplication by a reciprocal constant, not division) and, where a leaf
//! compares in decimal, converts via the `(decimal)double` cast which rounds to
//! 15 significant digits. Both quirks are reproduced here bit-for-bit.

use chrono::TimeDelta;
use rust_decimal::Decimal;

/// `ComparisonOps.Compare`: exact-decimal comparison; any operator other than
/// the five known strings (including a missing/null operator) yields false.
pub fn compare(actual: Decimal, op: Option<&str>, threshold: Decimal) -> bool {
    match op {
        Some("<") => actual < threshold,
        Some("<=") => actual <= threshold,
        Some(">") => actual > threshold,
        Some(">=") => actual >= threshold,
        Some("==") => actual == threshold,
        _ => false,
    }
}

const TICKS_PER_MINUTE: f64 = 600_000_000.0;
const TICKS_PER_HOUR: f64 = 36_000_000_000.0;
const TICKS_PER_DAY: f64 = 864_000_000_000.0;
const MINUTES_PER_TICK: f64 = 1.0 / TICKS_PER_MINUTE;
const HOURS_PER_TICK: f64 = 1.0 / TICKS_PER_HOUR;
const DAYS_PER_TICK: f64 = 1.0 / TICKS_PER_DAY;

/// .NET `TimeSpan` ticks (100 ns units) for a chrono duration.
pub fn ticks(d: TimeDelta) -> i64 {
    d.num_seconds() * 10_000_000 + i64::from(d.subsec_nanos()) / 100
}

/// `TimeSpan.TotalMinutes` (double): `ticks * MinutesPerTick`.
pub fn total_minutes(d: TimeDelta) -> f64 {
    ticks(d) as f64 * MINUTES_PER_TICK
}

/// `TimeSpan.TotalHours` (double): `ticks * HoursPerTick`.
pub fn total_hours(d: TimeDelta) -> f64 {
    ticks(d) as f64 * HOURS_PER_TICK
}

/// `TimeSpan.TotalDays` (double): `ticks * DaysPerTick`.
pub fn total_days(d: TimeDelta) -> f64 {
    ticks(d) as f64 * DAYS_PER_TICK
}

/// The C# `(decimal)double` cast: rounds the double to 15 significant digits
/// (round-to-nearest). Returns `None` where C# would throw (NaN/Inf/overflow);
/// callers map that to `false`, matching the engine's silent-fail mode.
pub fn decimal_from_f64_cs(v: f64) -> Option<Decimal> {
    if !v.is_finite() {
        return None;
    }
    if v == 0.0 {
        return Some(Decimal::ZERO);
    }
    // {:.14e} renders 15 significant digits, correctly rounded — the same
    // precision contract as System.Decimal's double constructor.
    Decimal::from_scientific(&format!("{v:.14e}")).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cast_rounds_to_15_significant_digits() {
        // 1/3 minute elapsed: double = 0.3333333333333333…, C# decimal cast
        // keeps 15 significant digits.
        let d = decimal_from_f64_cs(1.0 / 3.0).unwrap();
        assert_eq!(d.to_string(), "0.333333333333333");
    }

    #[test]
    fn whole_minutes_cast_exactly() {
        let d = TimeDelta::minutes(15);
        let cast = decimal_from_f64_cs(total_minutes(d)).unwrap();
        assert_eq!(cast, Decimal::from(15));
    }
}
