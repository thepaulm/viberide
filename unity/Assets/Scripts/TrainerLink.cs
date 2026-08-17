using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KickrWorld
{
    /// <summary>Wire format from the Python bridge. Field names must match the
    /// JSON exactly -- JsonUtility matches by name, not by attribute.</summary>
    [Serializable]
    public class Telemetry
    {
        public string type;
        public float t;
        public float power_w;
        public float cadence_rpm;
        public float speed_mps;
        public float speed_kph;
        public float distance_m;
        public float elevation_gain_m;
        public float grade;
        public float heart_rate_bpm;
        public string mode;
        public bool connected;
        public bool demo;
        public string trainer_status;
        public string trainer_detail;
        public string scan_seen;
        public int scan_count;
    }

    /// <summary>
    /// Talks to the Python bridge over a local WebSocket.
    ///
    /// The socket is serviced on a background task because Unity's main thread
    /// must never block on network I/O. Incoming messages land in a queue that
    /// Update() drains to the NEWEST message, discarding the rest -- the bridge
    /// broadcasts at 30 Hz and rendering may be faster or slower, and consuming
    /// one message per frame would build an ever-growing lag behind reality.
    /// </summary>
    public class TrainerLink : MonoBehaviour
    {
        [Header("Connection")]
        public string Url = "ws://127.0.0.1:8765";
        public bool AutoConnect = true;
        public float ReconnectSeconds = 3f;

        [Header("Live values")]
        public Telemetry Latest = new Telemetry();
        public bool Connected;

        readonly ConcurrentQueue<string> _inbox = new();
        readonly ConcurrentQueue<string> _outbox = new();
        ClientWebSocket _socket;
        CancellationTokenSource _cancel;
        Task _worker;
        float _retryAt;

        public event Action<Telemetry> OnTelemetry;

        void Start()
        {
            if (AutoConnect) Connect();
        }

        public void Connect()
        {
            if (_worker != null && !_worker.IsCompleted) return;
            _cancel = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cancel.Token));
        }

        async Task RunAsync(CancellationToken token)
        {
            try
            {
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri(Url), token);
                Connected = true;

                var send = SendLoopAsync(token);
                var recv = ReceiveLoopAsync(token);
                await Task.WhenAny(send, recv);
            }
            catch (OperationCanceledException) { }
            catch (Exception exc)
            {
                // Expected whenever the bridge isn't running yet; don't spam.
                Debug.Log($"[TrainerLink] not connected: {exc.Message}");
            }
            finally
            {
                Connected = false;
                try { _socket?.Dispose(); } catch { }
                _socket = null;
            }
        }

        async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                _inbox.Enqueue(sb.ToString());
                sb.Clear();
            }
        }

        async Task SendLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                if (_outbox.TryDequeue(out var msg))
                {
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    await _socket.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, token);
                }
                else
                {
                    await Task.Delay(10, token);
                }
            }
        }

        public void Send(string json)
        {
            // Cap the queue: if the socket stalls we want to drop stale grade
            // updates, not accumulate a backlog of them.
            if (_outbox.Count < 32) _outbox.Enqueue(json);
        }

        public void SendGrade(float grade)
        {
            Send($"{{\"type\":\"grade\",\"grade\":{grade.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        void Update()
        {
            // Drain to the newest message; older ones are already stale.
            string newest = null;
            while (_inbox.TryDequeue(out var msg)) newest = msg;

            if (newest != null)
            {
                try
                {
                    var t = JsonUtility.FromJson<Telemetry>(newest);
                    if (t != null && t.type == "telemetry")
                    {
                        Latest = t;
                        OnTelemetry?.Invoke(t);
                    }
                }
                catch (Exception exc)
                {
                    Debug.LogWarning($"[TrainerLink] bad telemetry: {exc.Message}");
                }
            }

            if (!Connected && AutoConnect && Time.time >= _retryAt)
            {
                _retryAt = Time.time + ReconnectSeconds;
                Connect();
            }
        }

        void OnDestroy() => Shutdown();
        void OnApplicationQuit() => Shutdown();

        void Shutdown()
        {
            try { _cancel?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
        }
    }
}
