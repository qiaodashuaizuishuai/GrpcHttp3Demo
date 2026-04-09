using GrpcHttp3Demo.Core;
using GrpcHttp3Demo.Endpoints.Grpc;
using GrpcHttp3Demo.Infrastructure.Background;
using GrpcHttp3Demo.Infrastructure.WebSockets;
using GrpcHttp3Demo.Utils;
using GrpcHttp3Demo.Infrastructure.Monitoring;

namespace GrpcHttp3Demo.Infrastructure.Composition
{
    /// <summary>
    /// 组合根（Composition Root）
    /// 只负责：决定“模块级顺序”，以及把 Builder/App 的配置集中管理。
    /// 不做反射扫描，不让业务类承担注册职责。
    /// </summary>
    public static class AppComposition
    {
        /// <summary>
        /// Builder 阶段：注册依赖注入（模块级顺序在这里显式体现）
        /// </summary>
        public static WebApplicationBuilder ConfigureModules(this WebApplicationBuilder builder)
        {
            // 一级顺序：模块注入顺序（显式、可控）
            builder.Services
                .AddGrpcModule()          // gRPC + HttpContextAccessor
                .AddCoreModule()          // Core 单例服务
                .AddBackgroundModule()    // 后台任务
                .AddMonitoringModule();   // 监控（Controller）

            return builder;
        }

        /// <summary>
        /// App 阶段：配置中间件与路由（模块级顺序在这里显式体现）
        /// </summary>
        public static WebApplication ConfigurePipeline(this WebApplication app)
        {
            // 一级顺序：中间件顺序（显式、可控）
            app.UseMiddlewareModule();

            // 一级顺序：路由/端点映射顺序（显式、可控）
            app.MapRoutesModule();

            return app;
        }

        /// <summary>
        /// 中间件模块：只放 app.UseXXX 这类管道配置。
        /// </summary>
        public static WebApplication UseMiddlewareModule(this WebApplication app)
        {
            app
                .UseAppConfigModule()
                .UseWebSocketsModule();

            return app;
        }

        /// <summary>
        /// 路由模块：只放 app.MapXXX 这类端点映射。
        /// </summary>
        public static WebApplication MapRoutesModule(this WebApplication app)
        {
            app
                .MapGrpcModule()
                .MapWebSocketsModule()
                .MapMonitoringModule();

            // 其它非模块化的简单路由也可以留在这里
            app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

            return app;
        }
    }
}
