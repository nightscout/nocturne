namespace Nocturne.Core.Models.V4;

public class PaginatedResponse<T>
{
    public IEnumerable<T> Data { get; set; } = [];
    public PaginationInfo Pagination { get; set; } = new();
}

public record PaginationInfo(int Limit = 100, int Offset = 0, int Total = 0);
