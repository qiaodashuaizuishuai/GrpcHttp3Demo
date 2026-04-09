using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using GrpcHttp3Demo.Core.Managers;
using GrpcHttp3Demo.Core.Services;
using GrpcHttp3Demo.Infrastructure.Udp;

namespace GrpcHttp3Demo.Infrastructure.Background
{
    public class UdpMediaServer : BackgroundService
    {
        private readonly UdpClient _udpServer;
        private readonly Socket _udpSocket;
        private readonly ConnectionManager _connectionManager;
        private readonly UdpMetricsService _metrics;
        private readonly ILogger<UdpMediaServer> _logger;
        private readonly int _port;

        private readonly UdpForwardingOptions _forwardingOptions;
        private UdpPerTargetSendQueue? _sendQueue;
        private UdpSendDispatcher? _sendDispatcher;

        // Optional data-plane keepalive ack (DACK): per remote endpoint, at most once per 5 seconds.
        private static readonly byte[] DackBytes = Encoding.UTF8.GetBytes("DACK");
        private readonly ConcurrentDictionary<IPEndPoint, long> _lastDackTickMs = new();

        public UdpMediaServer(ConnectionManager connectionManager, UdpMetricsService metrics, ILogger<UdpMediaServer> logger, IConfiguration configuration)
        {
            _connectionManager = connectionManager;
            _metrics = metrics;
            _logger = logger;
            _port = configuration.GetValue<int>("MediaServer:UdpPort", 7778);
            _forwardingOptions = configuration.GetSection("MediaServer:UdpForwarding").Get<UdpForwardingOptions>() ?? new UdpForwardingOptions();

            _udpServer = new UdpClient(_port);
            _udpSocket = _udpServer.Client;

            // Tune OS socket buffers to reduce drops under high bitrate/bursty send.
            // These are best-effort; OS may clamp to system limits.
            var recvBuf = configuration.GetValue<int?>("MediaServer:UdpSocket:ReceiveBufferBytes");
            if (recvBuf is int rb && rb > 0)
            {
                _udpSocket.ReceiveBufferSize = rb;
            }

            var sendBuf = configuration.GetValue<int?>("MediaServer:UdpSocket:SendBufferBytes");
            if (sendBuf is int sb && sb > 0)
            {
                _udpSocket.SendBufferSize = sb;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"UDP Media Server started on port {_port}");

            // Always decouple receive from send to avoid RX being blocked by TX.
            // The legacy per-target queue/pacing path is optional and can be enabled via config.
            _sendDispatcher = new UdpSendDispatcher(_udpSocket, _metrics, _logger, _forwardingOptions);
            _sendQueue = _forwardingOptions.Enabled
                ? new UdpPerTargetSendQueue(_udpSocket, _metrics, _logger, _forwardingOptions)
                : null;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpServer.ReceiveAsync(stoppingToken);
                    var buffer = result.Buffer;
                    var remoteEp = result.RemoteEndPoint;

                    if (buffer.Length == 0) continue;

                    // 1. Protocol Muxing based on first byte
                    // 'H' (72) = HELLO, 'P' (80) = PING
                    // 0x01 = Video, 0x02 = Pose, 0x03 = Feedback, 0x04 = Audio
                    byte prefix = buffer[0];
                    _metrics.RecordRxPacket(buffer.Length, prefix);

                    if (prefix == 'H' || prefix == 'P')
                    {
                        await HandleControlPacket(buffer, remoteEp);
                        continue;
                    }

                    // 2. Video/Data Packet Processing
                    var senderSessionId = _connectionManager.GetSessionByEndpoint(remoteEp);

                    // Data-plane keepalive ack (optional): do NOT update endpoint mapping; only ack for known/authenticated endpoints.
                    if (!string.IsNullOrEmpty(senderSessionId))
                    {
                        _connectionManager.UpdateUdpDataActivity(senderSessionId);
                        TryEnqueueDack(remoteEp);
                    }

                    // 3. VR feedback (0x03): fast path via VR-EP -> Robot-EP route
                    if (prefix == 0x03)
                    {
                        if (_connectionManager.TryGetFeedbackForward(remoteEp, out var robotEp, out var counter) && robotEp != null)
                        {
                            counter?.RecordFeedback(buffer.Length);
                            if (_sendQueue != null) _sendQueue.Enqueue(robotEp, buffer, prefix);
                            else _sendDispatcher?.Enqueue(robotEp, buffer, prefix);
                        }
                        continue;
                    }

                    // 4. Video from Robot (0x01): forward using prebuilt ep -> targets
                    if (prefix == 0x01)
                    {
                        if (_connectionManager.TryGetForwardTargets(remoteEp, out var targets))
                        {
                            foreach (var dest in targets)
                            {
                                dest.Counter.RecordVideo(buffer.Length);
                                if (_sendQueue != null) _sendQueue.Enqueue(dest.Endpoint, buffer, prefix);
                                else _sendDispatcher?.Enqueue(dest.Endpoint, buffer, prefix);
                            }
                        }
                    }

                    // 5. Pose from VR (0x02): forward using prebuilt ep -> pose targets
                    if (prefix == 0x02)
                    {
                        if (_connectionManager.TryGetPoseForwardTargets(remoteEp, out var targets))
                        {
                            foreach (var dest in targets)
                            {
                                dest.Counter.RecordPose(buffer.Length);
                                if (_sendQueue != null) _sendQueue.Enqueue(dest.Endpoint, buffer, prefix);
                                else _sendDispatcher?.Enqueue(dest.Endpoint, buffer, prefix);
                            }
                        }
                    }

                    // 6. Audio from publisher (0x04): forward using prebuilt ep -> audio targets
                    if (prefix == 0x04)
                    {
                        if (_connectionManager.TryGetAudioForwardTargets(remoteEp, out var targets))
                        {
                            foreach (var dest in targets)
                            {
                                dest.Counter.RecordAudio(buffer.Length);
                                if (_sendQueue != null) _sendQueue.Enqueue(dest.Endpoint, buffer, prefix);
                                else _sendDispatcher?.Enqueue(dest.Endpoint, buffer, prefix);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UDP Receive Error");
                }
            }

            if (_sendQueue != null)
            {
                await _sendQueue.StopAsync();
            }

            if (_sendDispatcher != null)
            {
                await _sendDispatcher.StopAsync();
            }
        }

        private void TryEnqueueDack(IPEndPoint remoteEp)
        {
            // Use monotonic tick count to avoid issues with wall-clock adjustments.
            var now = Environment.TickCount64;

            while (true)
            {
                if (_lastDackTickMs.TryGetValue(remoteEp, out var last))
                {
                    if (now - last < 5000)
                    {
                        return;
                    }

                    if (_lastDackTickMs.TryUpdate(remoteEp, now, last))
                    {
                        break;
                    }

                    continue;
                }

                if (_lastDackTickMs.TryAdd(remoteEp, now))
                {
                    break;
                }
            }

            _sendDispatcher?.Enqueue(remoteEp, DackBytes, DackBytes[0]);
        }

        private Task HandleControlPacket(byte[] buffer, IPEndPoint remoteEp)
        {
            try
            {
                string msg = Encoding.UTF8.GetString(buffer);
                string[] parts = msg.Split('|');
                
                // Format: TYPE|SessionId|Timestamp|Signature
                if (parts.Length != 4) return Task.CompletedTask;

                string type = parts[0]; // HELLO or PING
                string sessionId = parts[1];
                string timestampStr = parts[2];
                string signature = parts[3];

                // 只接受严格格式的控制包；其它一律静默丢弃（避免日志被乱码/探测包刷屏）。
                if (!string.Equals(type, "HELLO", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                if (!Guid.TryParse(sessionId, out _))
                {
                    return Task.CompletedTask;
                }

                if (!long.TryParse(timestampStr, out _))
                {
                    return Task.CompletedTask;
                }

                static bool IsHex(string s)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        var c = s[i];
                        var isHex =
                            (c >= '0' && c <= '9') ||
                            (c >= 'a' && c <= 'f') ||
                            (c >= 'A' && c <= 'F');
                        if (!isHex) return false;
                    }
                    return true;
                }

                if (signature.Length != 64 || !IsHex(signature))
                {
                    return Task.CompletedTask;
                }

                static string Redact(string value, int keep = 8)
                {
                    if (string.IsNullOrEmpty(value)) return "<empty>";
                    if (value.Length <= keep) return value;
                    return value.Substring(0, keep) + "...";
                }

                // 打印收到的控制包（脱敏：SessionId/Signature 都是敏感信息）
                // HELLO 通常是低频关键事件：Info；PING 较高频：Debug
                if (string.Equals(type, "HELLO", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"[UDP:{type}] from={remoteEp} session={Redact(sessionId)} ts={timestampStr} sig={Redact(signature)}");
                }
                else
                {
                    _logger.LogDebug($"[UDP:{type}] from={remoteEp} session={Redact(sessionId)} ts={timestampStr} sig={Redact(signature)}");
                }

                // 1. Find Session
                var session = _connectionManager.GetSession(sessionId);
                if (session == null)
                {
                    return Task.CompletedTask;
                }

                // 2. Validate Signature
                // Data = Type + SessionId + Timestamp
                string dataToSign = type + sessionId + timestampStr;
                if (!ValidateSignature(dataToSign, sessionId, signature))
                {
                    return Task.CompletedTask;
                }

                // 3. Validate Timestamp (Optional: prevent replay > 30s)
                if (long.TryParse(timestampStr, out long ts))
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (Math.Abs(now - ts) > 30)
                    {
                        return Task.CompletedTask;
                    }
                }

                // 4. Update Connection Info
                _connectionManager.RegisterUdpBySession(sessionId, remoteEp);
                
                // 5. Send Response (ACK or PONG)
                string responseType = type == "HELLO" ? "ACK" : "PONG";
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseType);

                _sendDispatcher?.Enqueue(remoteEp, responseBytes, responseBytes[0]);

                if (type == "HELLO")
                {
                    _logger.LogInformation($"UDP Handshake Success: {sessionId} at {remoteEp}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling control packet");
            }

            return Task.CompletedTask;
        }

        private static bool ValidateSignature(string data, string key, string providedSignature)
        {
            try
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                byte[] dataBytes = Encoding.UTF8.GetBytes(data);

                using var hmac = new HMACSHA256(keyBytes);
                byte[] hashBytes = hmac.ComputeHash(dataBytes);
                string computedSignature = Convert.ToHexString(hashBytes).ToLowerInvariant();

                return computedSignature == providedSignature.ToLowerInvariant();
            }
            catch
            {
                return false;
            }
        }

    }
}
