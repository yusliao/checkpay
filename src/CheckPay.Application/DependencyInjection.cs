using Microsoft.Extensions.DependencyInjection;

namespace CheckPay.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
