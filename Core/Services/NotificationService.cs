using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Core.Managers;

namespace GrpcHttp3Demo.Core.Services
{
    public class NotificationService
    {
        private readonly ConnectionManager _connectionManager;
        private readonly VideoConfigDeliveryService _videoDeliveryService;
        private readonly AudioConfigDeliveryService _audioDeliveryService;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(ConnectionManager connectionManager, VideoConfigDeliveryService videoDeliveryService, AudioConfigDeliveryService audioDeliveryService, ILogger<NotificationService> logger)
        {
            _connectionManager = connectionManager;
            _videoDeliveryService = videoDeliveryService;
            _audioDeliveryService = audioDeliveryService;
            _logger = logger;
        }

        public async Task SendPairRequestAsync(string senderSessionId, string targetSessionId)
        {
            await SendPairEventAsync(senderSessionId, targetSessionId, PairEvent.Types.Op.Request);
        }

        public async Task SendPairAcceptAsync(string senderSessionId, string targetSessionId)
        {
            await SendPairEventAsync(senderSessionId, targetSessionId, PairEvent.Types.Op.Accept);
        }

        public async Task SendPairRejectAsync(string senderSessionId, string targetSessionId)
        {
            await SendPairEventAsync(senderSessionId, targetSessionId, PairEvent.Types.Op.Reject);
        }

        public async Task SendUnpairAsync(string senderSessionId, string targetSessionId)
        {
            await SendPairEventAsync(senderSessionId, targetSessionId, PairEvent.Types.Op.Unpair);
        }

        private async Task SendPairEventAsync(string senderSessionId, string targetSessionId, PairEvent.Types.Op op)
        {
            var evt = new EventMessage
            {
                SenderSessionId = senderSessionId,
                TargetSessionId = targetSessionId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Pair = new PairEvent
                {
                    Op = op,
                    PeerSessionId = senderSessionId
                }
            };

            await _connectionManager.SendEventAsync(targetSessionId, evt);
            _logger.LogInformation($"[Notification] Sent PairEvent {op} from {senderSessionId} to {targetSessionId}");
        }

        public async Task SendVideoConfigAsync(string senderSessionId, VideoConfig config)
        {
            var targets = _connectionManager.GetVideoConfigTargets(senderSessionId);
            if (targets.Count == 0)
            {
                _logger.LogInformation($"[Notification] VideoConfig has no targets. Sender={senderSessionId}");
                return;
            }

            _logger.LogInformation($"[Notification] Forwarding VideoConfig from {senderSessionId} to {targets.Count} targets with reliability check.");

            foreach (var targetSessionId in targets)
            {
                await SendVideoConfigToTargetAsync(senderSessionId, targetSessionId, config);
            }
        }

        public async Task SendVideoConfigToTargetAsync(string senderSessionId, string targetSessionId, VideoConfig config)
        {
             // Capture loop variable
            var tid = targetSessionId;
            
            // Use a unique ID for this specific dispatch tracking
            var dispatchConfigId = Guid.NewGuid().ToString();
            
            // Clone and set new ID
            var configToSend = config.Clone();
            configToSend.ConfigId = dispatchConfigId;

            // Run reliability loop in background (fire-and-forget from Sender perspective)
            _ = Task.Run(async () => 
            {
                await _videoDeliveryService.SendReliablyAsync(
                    dispatchConfigId,
                    tid,
                    async () => 
                    {
                        var evt = new EventMessage
                        {
                            SenderSessionId = senderSessionId,
                            TargetSessionId = tid,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            VideoConfig = configToSend // use the cloned config with tracked ID
                        };
                        await _connectionManager.SendEventAsync(tid, evt);
                    },
                    async () => 
                    {
                            _logger.LogWarning($"[Notification] Disconnecting {tid} due to VideoConfig delivery failure.");
                            _connectionManager.UnregisterGrpc(tid);
                            await Task.CompletedTask;
                    }
                );
            });

            await Task.CompletedTask;
        }

        public async Task SendAudioConfigAsync(string senderSessionId, IReadOnlyCollection<string> targets, AudioConfig config)
        {
            if (targets.Count == 0)
            {
                _logger.LogInformation($"[Notification] AudioConfig has no targets. Sender={senderSessionId}");
                return;
            }

            _logger.LogInformation($"[Notification] Forwarding AudioConfig from {senderSessionId} to {targets.Count} targets with reliability check.");

            foreach (var targetSessionId in targets)
            {
                await SendAudioConfigToTargetAsync(senderSessionId, targetSessionId, config);
            }
        }

        public async Task SendAudioConfigToTargetAsync(string senderSessionId, string targetSessionId, AudioConfig config)
        {
            var tid = targetSessionId;

            var dispatchConfigId = Guid.NewGuid().ToString();

            var configToSend = config.Clone();
            configToSend.ConfigId = dispatchConfigId;

            _ = Task.Run(async () =>
            {
                await _audioDeliveryService.SendReliablyAsync(
                    dispatchConfigId,
                    tid,
                    async () =>
                    {
                        var evt = new EventMessage
                        {
                            SenderSessionId = senderSessionId,
                            TargetSessionId = tid,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            AudioConfig = configToSend
                        };
                        await _connectionManager.SendEventAsync(tid, evt);
                    },
                    async () =>
                    {
                        _logger.LogWarning($"[Notification] Disconnecting {tid} due to AudioConfig delivery failure.");
                        _connectionManager.UnregisterGrpc(tid);
                        await Task.CompletedTask;
                    }
                );
            });

            await Task.CompletedTask;
        }
    }
}
