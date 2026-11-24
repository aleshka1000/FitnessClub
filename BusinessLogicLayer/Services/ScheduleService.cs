using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Models;
using DataAccessLayer.UnitOfWork;
using System.Text.Json;

namespace BusinessLogicLayer.Services;

public class ScheduleService(IUnitOfWork unitOfWork) : IScheduleService
{
    public async Task<Result<ScheduleResult>> GetAsync(long userId, ScheduleQuery query, CancellationToken ct = default)
    {
        if (query.To <= query.From || query.Limit is < 1 or > 100)
            return Invalid();

        if (!TryParseContinuationToken(query, out var cursor))
            return Invalid();

        var rows = await unitOfWork.Class.GetScheduleAsync(userId, query.From, query.To, query.RoomId,
            cursor?.StartsAt, cursor?.Id ?? 0, query.Limit + 1, ct);
        var items = rows.Take(query.Limit).Select(x => new ScheduleItemResult(x.ClassId, x.Title,
                x.StartsAt, x.DurationMinutes, new RoomResult(x.RoomId, x.RoomName), x.Capacity,
                (int)Math.Max(0, x.Capacity - x.BookedSeats), (int)x.MySeats)).ToArray();
        var token = rows.Count > query.Limit
            ? CreateContinuationToken(query, items[^1])
            : null;

        return Result<ScheduleResult>.Success(new ScheduleResult(items, token));
    }

    private static bool TryParseContinuationToken(ScheduleQuery query, out ScheduleCursor? cursor)
    {
        cursor = null;

        if (query.ContinuationToken is null)
            return true;

        try
        {
            var encoded = query.ContinuationToken.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            cursor = JsonSerializer.Deserialize<ScheduleCursor>(bytes);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }

        return cursor is not null && cursor.Version == 1 && cursor.From == query.From && cursor.To == query.To
            && cursor.RoomId == query.RoomId && cursor.StartsAt >= query.From && cursor.StartsAt < query.To
            && cursor.StartsAt.Kind == DateTimeKind.Utc && cursor.Id > 0;
    }

    private static string CreateContinuationToken(ScheduleQuery query, ScheduleItemResult lastItem)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ScheduleCursor(1,
                    query.From, query.To, query.RoomId, lastItem.StartsAt, lastItem.ClassId)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static Result<ScheduleResult> Invalid() =>
        Result<ScheduleResult>.Failure(new BusinessError(BusinessErrorKind.Validation));

    private sealed record ScheduleCursor(
        int Version,
        DateTime From,
        DateTime To,
        long? RoomId,
        DateTime StartsAt,
        long Id);
}
