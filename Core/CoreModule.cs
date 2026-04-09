using GrpcHttp3Demo.Core.Managers;
using GrpcHttp3Demo.Core.Services;

namespace GrpcHttp3Demo.Core
{
    /// <summary>
    /// Core 模块：注册核心业务服务（模块内部顺序由模块自己决定）
    /// </summary>
    public static class CoreModule
    {
        public static IServiceCollection AddCoreModule(this IServiceCollection services)
        {
            // 二级顺序：模块内部依赖注入顺序（显式）
            services.AddSingleton<ConnectionManager>();
            services.AddSingleton<NotificationService>();
            services.AddSingleton<SignalingAppService>();
            services.AddSingleton<VideoConfigDeliveryService>();
            services.AddSingleton<AudioConfigDeliveryService>();
            services.AddSingleton<UdpMetricsService>();
            services.AddSingleton<SignalingMetricsService>();
            services.AddSingleton<UdpForwardingMetricsService>();

            return services;
        }
    }
}
