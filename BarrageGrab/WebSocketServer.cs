using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BarrageGrab
{
    /// <summary>
    /// 客户端连接信息（Fleck 兼容）
    /// </summary>
    public class ConnectionInfo
    {
        public string ClientIpAddress { get; }
        public int ClientPort { get; }

        public ConnectionInfo(string ipAddress, int port)
        {
            ClientIpAddress = ipAddress;
            ClientPort = port;
        }

        public override string ToString() => $"{ClientIpAddress}:{ClientPort}";
    }

    /// <summary>
    /// WebSocket 连接接口（Fleck 兼容）
    /// </summary>
    public interface IWebSocketConnection
    {
        bool IsAvailable { get; }
        ConnectionInfo ConnectionInfo { get; }
        event Action<string> OnMessage;
        event Action OnClose;
        event Action<byte[]> OnPing;
        event Action<byte[]> OnPong;
        event Action<Exception> OnError;
        void Send(string message);
        void Send(byte[] message);
        void SendPong(byte[] data);
        void Close();
        void Dispose();
    }

    /// <summary>
    /// WebSocket 连接实现
    /// </summary>
    internal class WebSocketConnection : IWebSocketConnection, IDisposable
    {
        private readonly WebSocket _webSocket;
        private readonly CancellationTokenSource _cts;
        private bool _isDisposed;

        public bool IsAvailable => _webSocket.State == WebSocketState.Open;
        public ConnectionInfo ConnectionInfo { get; }

        public event Action<string> OnMessage;
        public event Action OnClose;
        public event Action<byte[]> OnPing;
        public event Action<byte[]> OnPong;
        public event Action<Exception> OnError;

        public WebSocketConnection(WebSocket webSocket, ConnectionInfo connectionInfo)
        {
            _webSocket = webSocket;
            ConnectionInfo = connectionInfo;
            _cts = new CancellationTokenSource();
        }

        public async Task StartReceiving()
        {
            var buffer = new byte[8192];
            try
            {
                while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseAsync();
                        break;
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnMessage?.Invoke(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnMessage?.Invoke(message);
                    }
                    // 注意：Ping/Pong 在 .NET 6 中由 WebSocket 协议自动处理，
                    // 不通过 ReceiveAsync 返回，因此不需要处理 WebSocketMessageType.Ping
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
            finally
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                    OnClose?.Invoke();
                }
            }
        }

        public void Send(string message)
        {
            if (_webSocket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(message);
            Send(bytes);
        }

        public void Send(byte[] message)
        {
            if (_webSocket.State != WebSocketState.Open) return;
            try
            {
                _webSocket.SendAsync(
                    new ArraySegment<byte>(message),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                ).Wait(5000);
            }
            catch { }
        }

        public void SendPong(byte[] data)
        {
            if (_webSocket.State != WebSocketState.Open) return;
            try
            {
                // .NET 6 中不能直接发送 Pong 帧，使用 Close 帧作为替代
                _webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Close,
                    true,
                    CancellationToken.None
                ).Wait(5000);
            }
            catch { }
        }

        public void Close()
        {
            CloseAsync().Wait(1000);
        }

        private async Task CloseAsync()
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None
                    );
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts.Cancel();
            _cts.Dispose();
            try { _webSocket.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// WebSocket 服务器实现（Fleck 兼容）
    /// </summary>
    public class WebSocketServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<WebSocketConnection> _connections = new List<WebSocketConnection>();
        private CancellationTokenSource _cts;
        private Task _serverTask;
        private bool _isDisposed;

        public string Location { get; }

        public event Action<IWebSocketConnection> OnConnect;
        public event Action<IWebSocketConnection, string> OnMessage;
        public event Action<IWebSocketConnection> OnClose;
        public event Action<IWebSocketConnection, byte[]> OnPing;
        public event Action<IWebSocketConnection, byte[]> OnPong;
        public event Action<Exception> OnError;

        public WebSocketServer(string url)
        {
            Location = url;
            _listener = new HttpListener();
            _listener.Prefixes.Add(url);
        }

        public void Start(Action<IWebSocketConnection> onConnect)
        {
            OnConnect += onConnect;
            Start();
        }

        public void Start()
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        if (context.Request.IsWebSocketRequest)
                        {
                            _ = HandleWebSocket(context);
                        }
                        else
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                        }
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { }
                }
            });
        }

        private async Task HandleWebSocket(HttpListenerContext context)
        {
            var endpoint = context.Request.RemoteEndPoint;
            ConnectionInfo connectionInfo = new ConnectionInfo(endpoint.Address.ToString(), endpoint.Port);
            WebSocketConnection connection = null;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                connection = new WebSocketConnection(wsContext.WebSocket, connectionInfo);
                lock (_connections) { _connections.Add(connection); }

                connection.OnMessage += msg => OnMessage?.Invoke(connection, msg);
                connection.OnClose += () =>
                {
                    lock (_connections) { _connections.Remove(connection); }
                    OnClose?.Invoke(connection);
                };
                connection.OnPing += data => OnPing?.Invoke(connection, data);
                connection.OnPong += data => OnPong?.Invoke(connection, data);
                connection.OnError += ex => OnError?.Invoke(ex);

                OnConnect?.Invoke(connection);
                await connection.StartReceiving();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                connection?.Dispose();
            }
        }

        public void Broadcast(string message)
        {
            lock (_connections)
            {
                foreach (var conn in _connections)
                {
                    try { conn.Send(message); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts?.Cancel();
            _listener?.Stop();
            lock (_connections)
            {
                foreach (var conn in _connections)
                {
                    try { conn.Dispose(); } catch { }
                }
                _connections.Clear();
            }
            _cts?.Dispose();
            _listener?.Close();
        }
    }
}
