using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Grpc.Core;
using GrpcHttp3Demo.Core.Services;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Models;

namespace GrpcHttp3Demo.Core.Managers
{
    // 表与索引的集中声明
    public partial class ConnectionManager
    {
        // 统一会话表: SessionId -> DeviceContext (主数据表，低频写，高频读；DeviceId仅展示)
        private readonly ConcurrentDictionary<string, DeviceContext> _sessions = new();

        // 高频索引: IPEndPoint -> SessionId (UDP热路径：O(1) 身份解析)
        private readonly ConcurrentDictionary<IPEndPoint, string> _endpointIndex = new();

        // 高频索引: SessionId -> IPEndPoint (UDP热路径：O(1) 获取对端地址)
        private readonly ConcurrentDictionary<string, IPEndPoint> _sessionEndpointIndex = new();

        // 双向配对表: SessionId <-> SessionId (存两份: A->B 和 B->A)
        private readonly ConcurrentDictionary<string, string> _pairings = new();

        // 订阅表: PublisherSessionId -> (SubscriberSessionId -> SubscriptionMeta)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SubscriptionMeta>> _subscriptionMeta = new();

        // 身份索引（信令层）：(DeviceId, Role) -> SessionId
        // 用于处理“短时间重复注册”去重：踢旧保新。
        private readonly ConcurrentDictionary<IdentityKey, string> _identityIndex = new();

        // 身份粒度锁：避免同一身份并发注册的竞态（比如同时 gRPC/WS 注册）。
        private readonly ConcurrentDictionary<IdentityKey, object> _identityGates = new();

        // 高频转发表: SrcEp -> ImmutableArray<UdpForwardTarget>
        // 重要：ForwardEdgeCounter 在转发表重建阶段预绑定，UDP 热路径只做 Interlocked 计数。
        private readonly ConcurrentDictionary<IPEndPoint, ImmutableArray<UdpForwardTarget>> _forwardingTable = new();

        // 高频转发表(Pose): SrcEp -> ImmutableArray<UdpForwardTarget>
        private readonly ConcurrentDictionary<IPEndPoint, ImmutableArray<UdpForwardTarget>> _poseForwardingTable = new();

        // 高频转发表(Audio): SrcEp -> ImmutableArray<UdpForwardTarget>
        private readonly ConcurrentDictionary<IPEndPoint, ImmutableArray<UdpForwardTarget>> _audioForwardingTable = new();

        // 反馈快速路由: VREndpoint -> RobotEndpoint + Counter（仅用于0x03反馈透明转发）
        private readonly ConcurrentDictionary<IPEndPoint, UdpFeedbackForwardTarget> _feedbackRoute = new();

        // P2P 共享密钥（配对粒度）：PairKey(a|b ordered) -> random key bytes
        private readonly ConcurrentDictionary<string, byte[]> _p2pSharedKeys = new();
    }
}
