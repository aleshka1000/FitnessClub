using BusinessLogicLayer.Interfaces;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;

namespace BusinessLogicLayer.Services;

public class WaitlistPromotionService(IUnitOfWork unitOfWork) : IWaitlistPromotionService
{
    public async Task PromoteAsync(Class scheduledClass, DateTime now, CancellationToken ct = default)
    {
        if (scheduledClass.IsCancelled || now >= scheduledClass.StartsAt)
            return;

        var freeSeats = scheduledClass.Capacity - await unitOfWork.Enrollment.GetActiveSeatsAsync(
            scheduledClass.Id,
            ct);
        DateTime? afterCreatedAt = null;
        long afterId = 0;
        var stop = false;

        while (!stop)
        {
            var batch = await unitOfWork.Waitlist.GetWaitingBatchAsync(scheduledClass.Id, afterCreatedAt, afterId, ct);

            // решение: если первому не хватает мест, останавливаем очередь до проверки его абонемента.
            // Так меньшие заявки не обходят первого, даже если его абонемент уже истёк.
            if (batch.Count == 0 || batch[0].Seats > freeSeats)
                break;

            var userIds = batch.Where(x => x.Seats <= freeSeats).Select(x => x.UserId).Distinct().ToArray();
            var passes = (await unitOfWork.Pass.GetByUsersAsync(userIds, ct)).ToDictionary(x => x.UserId);

            foreach (var entry in batch)
            {
                if (entry.Seats > freeSeats)
                {
                    stop = true;
                    break;
                }

                var pass = passes[entry.UserId];
                var reason = pass.ValidUntil < DateOnly.FromDateTime(now) ? "pass-expired"
                    : pass.RemainingVisits < entry.Seats ? "pass-exhausted" : null;

                if (reason is not null)
                {
                    // решение: неподходящий абонемент завершает ожидание как skipped, чтобы не блокировать следующих.
                    // Причину сохраняем в истории; автоматически в очередь запись не возвращаем.
                    entry.Status = "skipped";
                    entry.SkipReason = reason;
                }
                else
                {
                    pass.RemainingVisits -= entry.Seats;
                    entry.PromotedEnrollment = await unitOfWork.Enrollment.CreateAsync(new Enrollment
                        {
                            UserId = entry.UserId,
                            ClassId = entry.ClassId,
                            Seats = entry.Seats,
                            Status = "active",
                            CreatedAt = now
                        }, ct);
                    entry.Status = "promoted";
                    freeSeats -= entry.Seats;
                }

                entry.FinishedAt = now;
            }

            afterCreatedAt = batch[^1].CreatedAt;
            afterId = batch[^1].Id;
        }

        await unitOfWork.SaveChangesAsync(ct);
    }
}
