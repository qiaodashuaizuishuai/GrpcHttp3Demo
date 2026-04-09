namespace GrpcHttp3Demo.Infrastructure.Background
{
    /// <summary>
    /// 后台任务模块：注册 HostedService
    /// </summary>
    public static class BackgroundModule
    {
        public static IServiceCollection AddBackgroundModule(this IServiceCollection services)
        {
            // 二级顺序：模块内部注册顺序（显式）
            services.AddHostedService<UdpMediaServer>();
            services.AddHostedService<SessionCleanupService>();

            return services;
        }
    }
}
