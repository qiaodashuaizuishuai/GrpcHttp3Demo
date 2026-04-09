using Grpc.Core;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Core.Interfaces;
using GrpcHttp3Demo.Core.Services;

namespace GrpcHttp3Demo.Infrastructure.Grpc
{
    public class GrpcEventStreamSender : IEventStreamSender
    {
        private readonly IServerStreamWriter<EventMessage> _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SignalingMetricsService? _metrics;

        public GrpcEventStreamSender(IServerStreamWriter<EventMessage> stream, SignalingMetricsService? metrics = null)
        {
            _stream = stream;
            _metrics = metrics;
        }

        public Task WriteAsync(EventMessage message)
        {
            return WriteInternalAsync(message);
        }

        private async Task WriteInternalAsync(EventMessage message)
        {
            await _sendLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(message);
                _metrics?.RecordOutboundEvent(message);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
