using GrpcHttp3Demo.Protos;

namespace GrpcHttp3Demo.Core.Interfaces
{
    public interface IEventStreamSender
    {
        Task WriteAsync(EventMessage message);
    }
}
