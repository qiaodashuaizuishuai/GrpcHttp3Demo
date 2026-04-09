using System;

namespace GrpcHttp3Demo.Infrastructure.Udp
{
    internal sealed class UdpForwardingOptions
    {
        public bool Enabled { get; init; } = false;
        public int QueueCapacityPerTarget { get; init; } = 2048;
        public int MaxPpsPerTarget { get; init; } = 0;
        public int MaxBpsPerTarget { get; init; } = 0;
        public bool RetryOnNoBuffer { get; init; } = true;
        public int MaxRetries { get; init; } = 1;
        public int RetryDelayMs { get; init; } = 1;
    }
}
