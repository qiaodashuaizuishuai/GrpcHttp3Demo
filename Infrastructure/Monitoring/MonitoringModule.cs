using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace GrpcHttp3Demo.Infrastructure.Monitoring
{
    public static class MonitoringModule
    {
        public static IServiceCollection AddMonitoringModule(this IServiceCollection services)
        {
            services.AddControllers();
            return services;
        }

        public static IEndpointRouteBuilder MapMonitoringModule(this IEndpointRouteBuilder app)
        {
            app.MapControllers();
            return app;
        }
    }
}
