using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>A soft-deleted state span a caller named.</summary>
/// <param name="SentId">The id the caller sent for it.</param>
/// <param name="Category">The span's category.</param>
public sealed record DeletedStateSpan(string SentId, StateSpanCategory Category);
