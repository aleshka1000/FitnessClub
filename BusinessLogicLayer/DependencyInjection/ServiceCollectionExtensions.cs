using BusinessLogicLayer.Interfaces;
using BusinessLogicLayer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessLogicLayer.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBusinessLogicLayer(this IServiceCollection services)
    {
        services.AddScoped<IEnrollmentService, EnrollmentService>();
        services.AddScoped<IWaitlistService, WaitlistService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        return services;
    }
}
