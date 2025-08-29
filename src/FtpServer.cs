using System.Net;
using System.Net.Sockets;

namespace XTFtpServer
{
    public class FtpServer
    {
        private TcpListener _listener;
        private bool _isRunning;
        private readonly IFtpUserAuthentication _userAuth;
        private readonly string _rootDirectory;
        private readonly List<FtpClientSession> _sessions;
        private readonly int _port;

        public event EventHandler<string>? LogMessage;

        public FtpServer(IFtpUserAuthentication userAuth, string rootDirectory, int port = 21)
        {
            _userAuth = userAuth;
            _rootDirectory = rootDirectory;
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _sessions = new List<FtpClientSession>();
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _isRunning = true;

            OnLogMessage($"FTP Server started on port {_port}");

            while (_isRunning)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    var session = new FtpClientSession(tcpClient, _userAuth, _rootDirectory);
                    session.LogMessage += (sender, message) => OnLogMessage(message);

                    _sessions.Add(session);
                    _ = HandleClientSessionAsync(session);
                }
                catch (Exception ex)
                {
                    OnLogMessage($"Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientSessionAsync(FtpClientSession session)
        {
            try
            {
                await session.HandleSessionAsync();
            }
            finally
            {
                _sessions.Remove(session);
                session.Dispose();
            }
        }

        public async Task StopAsync()
        {
            _isRunning = false;
            _listener.Stop();

            foreach (var session in _sessions.ToList())
            {
                await session.CloseAsync();
            }

            _sessions.Clear();
            OnLogMessage("FTP Server stopped");
        }

        protected virtual void OnLogMessage(string message)
        {
            LogMessage?.Invoke(this, message);
        }
    }
}