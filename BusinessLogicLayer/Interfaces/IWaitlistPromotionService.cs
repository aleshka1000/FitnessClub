using DataAccessLayer.Entities;

namespace BusinessLogicLayer.Interfaces;

public interface IWaitlistPromotionService
{
    Task PromoteAsync(Class scheduledClass, DateTime now, CancellationToken ct = default);
}
