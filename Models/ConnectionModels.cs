using System;
using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Core.Interfaces;

namespace GrpcHttp3Demo.Models
{
    public class DeviceContext
    {
        // --- 自身信息 ---
        public string DeviceId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public RegisterRequest.Types.EndpointType Role { get; set; }
        
        // --- 网络信息 ---
        public IEventStreamSender? EventSender { get; set; }
        public IPEndPoint? UdpEndpoint { get; set; }
        public string ClientIp { get; set; } = string.Empty;
        public int ClientPort { get; set; }
        public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

        // --- UDP 活跃性 ---
        // 只由 UDP 控制面（HELLO/PING 且验签通过）更新映射；
        // 数据面只记录活跃时间，不参与映射更新。
        public DateTime LastUdpControlUtc { get; set; } = DateTime.MinValue;
        public DateTime LastUdpDataUtc { get; set; } = DateTime.MinValue;

        // UDP 映射救援状态（通过 push 通道提示客户端重发 UDP HELLO）
        public int UdpRescueCount { get; set; } = 0;
        public DateTime LastUdpRescueUtc { get; set; } = DateTime.MinValue;
        
        // --- 状态机 ---
        public int GrpcRescueCount { get; set; } = 0;

        // --- 配对信息 (1:1) ---
        public string? PairedDeviceId { get; set; }
        
        // --- 缓存信息 ---
        public VideoConfig? LastVideoConfig { get; set; }
        public AudioConfig? LastAudioConfig { get; set; }

        // --- 订阅信息 (谁订阅了我) ---
        // Key: SubscriberId, Value: 订阅详情
        public ConcurrentDictionary<string, SubscriptionDetail> Subscribers { get; } = new();
    }

    public class SubscriptionDetail
    {
        public string SubscriberId { get; set; } = string.Empty;
        public bool SubVideo { get; set; }
        public bool SubPose { get; set; }
        public bool SubAudio { get; set; }
    }

    // 低频、富信息的订阅元数据，供管理/控制面使用
    public class SubscriptionMeta
    {
        public string SubscriberId { get; set; } = string.Empty;
        public bool SubVideo { get; set; }
        public bool SubPose { get; set; }
        public bool SubAudio { get; set; }
        public byte[]? Sps { get; set; }
        public byte[]? Pps { get; set; }
        public float? TargetBitrateKbps { get; set; }
        public float? SubscriberBandwidthKbps { get; set; }
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    // SCReAM 热状态：最小化，只存建议码率与时间戳（session级）
    public readonly record struct ScreamHotState(float BitrateKbps, long UpdatedAtMs);
}
