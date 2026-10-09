namespace HR.Infrastructure;

/// <summary>One page of a list query.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    /// <summary>Clamps a requested page into 1..last for a result of <paramref name="totalCount"/> rows.</summary>
    public static int ClampPage(int requested, int totalCount, int pageSize) =>
        Math.Clamp(requested, 1, Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));
}
