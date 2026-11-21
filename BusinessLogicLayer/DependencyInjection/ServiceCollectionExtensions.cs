using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BusinessLogicLayer.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessLogicLayer(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IEnrollmentService, EnrollmentService>();
        services.AddScoped<IWaitlistService, WaitlistService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        return services;
    }
}
