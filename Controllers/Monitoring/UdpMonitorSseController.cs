using System.Text.Json;
using GrpcHttp3Demo.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace GrpcHttp3Demo.Controllers.Monitoring
{
    [ApiController]
    public sealed class UdpMonitorSseController : ControllerBase
    {
        private readonly UdpMetricsService _metrics;
        private readonly IHostEnvironment _env;
        private readonly IConfiguration _configuration;

        public UdpMonitorSseController(UdpMetricsService metrics, IHostEnvironment env, IConfiguration configuration)
        {
            _metrics = metrics;
            _env = env;
            _configuration = configuration;
        }

        // Server-Sent Events stream: pushes the same snapshot periodically.
        // Query: intervalMs (optional, default 1000, range 250..10000)
        [HttpGet("/api/monitor/udp/stream")]
        public async Task Stream([FromQuery] int? intervalMs, CancellationToken ct)
        {
            var monitoringEnabled = _env.IsDevelopment() || _configuration.GetValue<bool>("Monitoring:Enabled", false);
            if (!monitoringEnabled)
            {
                Response.StatusCode = 404;
                return;
            }

            var interval = Math.Clamp(intervalMs ?? 1000, 250, 10_000);

            // Treat client disconnect/abort as a normal termination condition.
            var abortToken = HttpContext?.RequestAborted ?? ct;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, abortToken);
            var token = linkedCts.Token;

            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";
            Response.ContentType = "text/event-stream";

            try
            {
                // Initial comment to open the stream promptly
                await Response.WriteAsync(": ok\n\n", token);
                await Response.Body.FlushAsync(token);

                while (!token.IsCancellationRequested)
                {
                    var payload = JsonSerializer.Serialize(_metrics.Snapshot());
                    await Response.WriteAsync("event: udp\n", token);
                    await Response.WriteAsync($"data: {payload}\n\n", token);
                    await Response.Body.FlushAsync(token);
                    await Task.Delay(interval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Normal: browser closed EventSource / request aborted / client stopped.
            }
            catch (IOException)
            {
                // Normal: client disconnected while writing.
            }
            catch (ObjectDisposedException)
            {
                // Normal: response stream disposed during shutdown.
            }
        }
    }
}
