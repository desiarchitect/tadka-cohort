namespace Tadka.Api.Contracts;

/// <summary>
/// Day 5, Beat 4: keyset ("cursor") pagination — the fix for OFFSET's "page 10,000" cost.
/// No TotalCount / TotalPages here on purpose: computing a total means scanning (or at least
/// counting) the whole matching set, which is exactly the cost keyset pagination exists to
/// avoid. If a caller needs a total, that is a different, explicitly-paid-for query.
/// </summary>
public record CursorPageResponse<T>(
    List<T> Items,
    string? NextCursor);
