namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A client-supplied free-text column whose value is cut to the column's <c>MaxLength</c> on save
/// rather than failing the write. Nightscout stores any length, so a record must not be refused
/// on every resend for a label too long to hold; losing the tail of the label is the lesser loss.
/// The cut never splits a surrogate pair. Identifiers are never marked: cutting one would let two
/// records collide on it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FitToColumnAttribute : Attribute { }
