namespace GrpcHttp3Demo.Endpoints.Grpc
{
    /// <summary>
    /// gRPC 模块：注册 gRPC 与映射 gRPC 服务
    /// </summary>
    public static class GrpcModule
    {
        public static IServiceCollection AddGrpcModule(this IServiceCollection services)
        {
            // 二级顺序：模块内部注册顺序（显式）
            services.AddGrpc();
            services.AddHttpContextAccessor();

            return services;
        }

        public static WebApplication MapGrpcModule(this WebApplication app)
        {
            app.MapGrpcService<SignalingService>();
            return app;
        }
    }
}
