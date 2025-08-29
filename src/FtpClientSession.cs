using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace XTFtpServer
{
    public class FtpClientSession : IDisposable
    {
        private readonly TcpClient _controlClient;
        private readonly NetworkStream _controlStream;
        private readonly StreamReader _controlReader;
        private readonly StreamWriter _controlWriter;
        private readonly IFtpUserAuthentication _userAuth;
        private readonly string _rootDirectory;

        private TcpListener? _dataListener;
        private TcpClient? _dataClient;
        private string _currentDirectory;
        private string? _username;
        private bool _authenticated;
        private string _renameFrom = string.Empty;
        private long _restartPosition = 0;
        private bool _passiveMode = false;
        private IPEndPoint? _passiveEndpoint;

        public event EventHandler<string>? LogMessage;

        public FtpClientSession(TcpClient client, IFtpUserAuthentication userAuth, string rootDirectory)
        {
            _controlClient = client;
            _controlStream = client.GetStream();

            // 使用ASCII编码，这是FTP标准
            _controlReader = new StreamReader(_controlStream, Encoding.ASCII, false, 1024, true);
            _controlWriter = new StreamWriter(_controlStream, Encoding.ASCII, 1024, true);
            _controlWriter.NewLine = "\r\n"; // FTP标准换行符

            _userAuth = userAuth;
            _rootDirectory = Path.GetFullPath(rootDirectory);
            _currentDirectory = "/";

            // 设置超时
            _controlStream.ReadTimeout = 60000;
            _controlStream.WriteTimeout = 60000;
        }

        public async Task HandleSessionAsync()
        {
            try
            {
                OnLogMessage($"New client connected from: {_controlClient.Client.RemoteEndPoint}");

                // 发送欢迎消息
                await SendResponseAsync(220, "Welcome to XTFtpServer");

                while (_controlClient.Connected && _controlClient.Client.Connected)
                {
                    try
                    {
                        OnLogMessage("Waiting for command...");

                        // 检查流状态
                        if (!_controlStream.CanRead)
                        {
                            OnLogMessage("Control stream is no longer readable");
                            break;
                        }

                        var commandLine = await _controlReader.ReadLineAsync();

                        OnLogMessage($"ReadLine returned. CommandLine is {(commandLine == null ? "null" : $"'{commandLine}' (length: {commandLine?.Length})")}");

                        if (commandLine == null)
                        {
                            OnLogMessage("Client disconnected (connection closed by client)");
                            break;
                        }

                        if (string.IsNullOrEmpty(commandLine))
                        {
                            OnLogMessage("Empty command received, continuing...");
                            continue;
                        }

                        OnLogMessage($"Processing command: {commandLine}");
                        await ProcessCommandAsync(commandLine);
                    }
                    catch (IOException ex)
                    {
                        OnLogMessage($"IO Error reading command: {ex.Message}");
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        OnLogMessage("Connection stream disposed");
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnLogMessage($"Error reading command: {ex.Message}");
                        await Task.Delay(100);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"Session error: {ex.Message}");
            }
            finally
            {
                OnLogMessage("Closing session");
                await CloseAsync();
            }
        }

        private async Task ProcessCommandAsync(string commandLine)
        {
            var parts = commandLine.Split(' ', 2);
            var command = parts[0].ToUpper();
            var arguments = parts.Length > 1 ? parts[1] : string.Empty;

            OnLogMessage($"Command: {command} {arguments}");

            switch (command)
            {
                case "USER":
                    await HandleUserCommandAsync(arguments);
                    break;
                case "PASS":
                    await HandlePassCommandAsync(arguments);
                    break;
                case "QUIT":
                    await HandleQuitCommandAsync();
                    break;
                case "PWD":
                case "XPWD":
                    await HandlePwdCommandAsync();
                    break;
                case "CWD":
                    await HandleCwdCommandAsync(arguments);
                    break;
                case "CDUP":
                    await HandleCdupCommandAsync();
                    break;
                case "PORT":
                    await HandlePortCommandAsync(arguments);
                    break;
                case "PASV":
                    await HandlePasvCommandAsync();
                    break;
                case "LIST":
                    await HandleListCommandAsync(arguments);
                    break;
                case "NLST":
                    await HandleNlstCommandAsync(arguments);
                    break;
                case "RETR":
                    await HandleRetrCommandAsync(arguments);
                    break;
                case "STOR":
                    await HandleStorCommandAsync(arguments);
                    break;
                case "APPE":
                    await HandleAppeCommandAsync(arguments);
                    break;
                case "REST":
                    await HandleRestCommandAsync(arguments);
                    break;
                case "SIZE":
                    await HandleSizeCommandAsync(arguments);
                    break;
                case "MDTM":
                    await HandleMdtmCommandAsync(arguments);
                    break;
                case "DELE":
                    await HandleDeleCommandAsync(arguments);
                    break;
                case "RMD":
                    await HandleRmdCommandAsync(arguments);
                    break;
                case "MKD":
                case "XMKD":
                    await HandleMkdCommandAsync(arguments);
                    break;
                case "RNFR":
                    await HandleRnfrCommandAsync(arguments);
                    break;
                case "RNTO":
                    await HandleRntoCommandAsync(arguments);
                    break;
                case "TYPE":
                    await HandleTypeCommandAsync(arguments);
                    break;
                case "SYST":
                    await HandleSystCommandAsync();
                    break;
                case "NOOP":
                    await HandleNoopCommandAsync();
                    break;
                default:
                    await SendResponseAsync(502, $"Command not implemented: {command}");
                    break;
            }
        }

        private async Task HandleUserCommandAsync(string username)
        {
            _username = username;
            await SendResponseAsync(331, "Username OK, need password");
        }

        private async Task HandlePassCommandAsync(string password)
        {
            if (string.IsNullOrEmpty(_username))
            {
                await SendResponseAsync(503, "Login with USER first");
                return;
            }

            if (_userAuth.AuthenticateUser(_username, password))
            {
                _authenticated = true;
                await SendResponseAsync(230, "Login successful");
            }
            else
            {
                await SendResponseAsync(530, "Login incorrect");
            }
        }

        private async Task HandleQuitCommandAsync()
        {
            await SendResponseAsync(221, "Goodbye");
            await CloseAsync();
        }

        private async Task HandlePwdCommandAsync()
        {
            if (!await CheckAuthenticationAsync())
                return;

            await SendResponseAsync(257, $"\"{_currentDirectory}\" is current directory");
        }

        private async Task HandleCwdCommandAsync(string path)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var newPath = ResolvePath(path);
                if (Directory.Exists(newPath))
                {
                    _currentDirectory = path.StartsWith("/") ? path : CombinePath(_currentDirectory, path);
                    // Normalize path
                    if (!_currentDirectory.StartsWith("/"))
                        _currentDirectory = "/" + _currentDirectory;
                    if (_currentDirectory.Length > 1 && _currentDirectory.EndsWith("/"))
                        _currentDirectory = _currentDirectory.TrimEnd('/');

                    await SendResponseAsync(250, "Directory changed");
                }
                else
                {
                    await SendResponseAsync(550, "Directory not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to change directory");
            }
        }

        private async Task HandleCdupCommandAsync()
        {
            if (!await CheckAuthenticationAsync())
                return;

            if (_currentDirectory != "/")
            {
                var parentDir = Path.GetDirectoryName(_currentDirectory.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parentDir))
                    parentDir = "/";
                else
                    parentDir = parentDir.Replace(Path.DirectorySeparatorChar, '/');

                _currentDirectory = parentDir;
            }

            await SendResponseAsync(250, "Directory changed");
        }

        private async Task HandlePortCommandAsync(string parameters)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                // Close any existing data connection
                await CloseDataConnectionAsync();

                // Parse host and port from parameters
                var parts = parameters.Split(',');
                if (parts.Length != 6)
                {
                    await SendResponseAsync(501, "Invalid PORT format");
                    return;
                }

                var host = $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}";
                var port = (int.Parse(parts[4]) << 8) + int.Parse(parts[5]);

                // Create endpoint for active mode
                var ip = IPAddress.Parse(host);
                _passiveEndpoint = new IPEndPoint(ip, port);
                _passiveMode = false;

                await SendResponseAsync(200, "PORT command successful");
            }
            catch (Exception ex)
            {
                OnLogMessage($"PORT command error: {ex.Message}");
                await SendResponseAsync(501, "Failed to establish data connection");
            }
        }

        private async Task HandlePasvCommandAsync()
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                // Close any existing data connection
                await CloseDataConnectionAsync();

                // Find available port
                Random random = new Random();
                TcpListener? listener = null;
                int port = 0;

                // Try to find an available port
                for (int attempts = 0; attempts < 10; attempts++)
                {
                    port = random.Next(1024, 65535);
                    try
                    {
                        listener = new TcpListener(IPAddress.Any, port);
                        listener.Start(1);
                        break;
                    }
                    catch
                    {
                        listener?.Stop();
                        listener = null;
                    }
                }

                if (listener == null)
                {
                    await SendResponseAsync(501, "Failed to enter passive mode");
                    return;
                }

                _dataListener = listener;
                port = ((IPEndPoint)_dataListener.LocalEndpoint).Port;

                // Convert IP and port to FTP format
                var localEndpoint = (IPEndPoint)_controlClient.Client.LocalEndPoint!;
                var ip = localEndpoint.Address.ToString();
                var ipParts = ip.Split('.').Select(int.Parse).ToArray();
                var p1 = port >> 8;
                var p2 = port & 0xFF;

                var response = $"Entering Passive Mode ({ipParts[0]},{ipParts[1]},{ipParts[2]},{ipParts[3]},{p1},{p2})";
                _passiveMode = true;
                await SendResponseAsync(227, response);

                OnLogMessage($"PASV mode enabled on port {port}");
            }
            catch (Exception ex)
            {
                OnLogMessage($"PASV command error: {ex.Message}");
                await SendResponseAsync(501, "Failed to enter passive mode");
            }
        }

        private async Task HandleListCommandAsync(string parameters)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var dataStream = await OpenDataStreamAsync();
                if (dataStream == null)
                {
                    await SendResponseAsync(425, "Can't open data connection");
                    return;
                }

                await SendResponseAsync(150, "Opening ASCII mode data connection for file list");

                using (dataStream)
                using (var writer = new StreamWriter(dataStream, Encoding.ASCII))
                {
                    writer.NewLine = "\r\n";

                    var path = ResolvePath(_currentDirectory);
                    if (Directory.Exists(path))
                    {
                        // List directories
                        foreach (var dir in Directory.GetDirectories(path))
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            var line = FormatListLine(dirInfo.Name, dirInfo.LastWriteTime, dirInfo.Attributes, 0);
                            OnLogMessage(line);
                            await writer.WriteLineAsync(line);
                        }

                        // List files
                        foreach (var file in Directory.GetFiles(path))
                        {
                            var fileInfo = new FileInfo(file);
                            var line = FormatListLine(fileInfo.Name, fileInfo.LastWriteTime, fileInfo.Attributes, fileInfo.Length);
                            OnLogMessage(line);
                            await writer.WriteLineAsync(line);
                        }

                        await writer.FlushAsync();
                    }
                }

                await CloseDataConnectionAsync();
                await SendResponseAsync(226, "Transfer complete");
            }
            catch (Exception ex)
            {
                await CloseDataConnectionAsync();
                OnLogMessage($"LIST command error: {ex.Message}");
                await SendResponseAsync(550, $"Failed to list directory: {ex.Message}");
            }
        }

        private async Task HandleNlstCommandAsync(string parameters)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var dataStream = await OpenDataStreamAsync();
                if (dataStream == null)
                {
                    await SendResponseAsync(425, "Can't open data connection");
                    return;
                }

                await SendResponseAsync(150, "Opening ASCII mode data connection for name list");

                using (dataStream)
                using (var writer = new StreamWriter(dataStream, Encoding.ASCII))
                {
                    writer.NewLine = "\r\n";

                    var path = ResolvePath(_currentDirectory);
                    if (Directory.Exists(path))
                    {
                        // List directories
                        foreach (var dir in Directory.GetDirectories(path))
                        {
                            await writer.WriteLineAsync(Path.GetFileName(dir));
                        }

                        // List files
                        foreach (var file in Directory.GetFiles(path))
                        {
                            await writer.WriteLineAsync(Path.GetFileName(file));
                        }

                        await writer.FlushAsync();
                    }
                }

                await CloseDataConnectionAsync();
                await SendResponseAsync(226, "Transfer complete");
            }
            catch (Exception ex)
            {
                await CloseDataConnectionAsync();
                OnLogMessage($"NLST command error: {ex.Message}");
                await SendResponseAsync(550, $"Failed to list names: {ex.Message}");
            }
        }

        private async Task HandleRetrCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                if (!File.Exists(fullPath))
                {
                    await SendResponseAsync(550, "File not found");
                    return;
                }

                var dataStream = await OpenDataStreamAsync();
                if (dataStream == null)
                {
                    await SendResponseAsync(425, "Can't open data connection");
                    return;
                }

                await SendResponseAsync(150, "Opening binary mode data connection for file transfer");

                using (dataStream)
                using (var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Handle restart position
                    if (_restartPosition > 0)
                    {
                        fileStream.Seek(_restartPosition, SeekOrigin.Begin);
                        _restartPosition = 0;
                    }

                    await fileStream.CopyToAsync(dataStream);
                }

                await CloseDataConnectionAsync();
                await SendResponseAsync(226, "Transfer complete");
            }
            catch (Exception ex)
            {
                await CloseDataConnectionAsync();
                OnLogMessage($"RETR command error: {ex.Message}");
                await SendResponseAsync(550, $"Failed to retrieve file: {ex.Message}");
            }
        }

        private async Task HandleStorCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                var directory = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var dataStream = await OpenDataStreamAsync();
                if (dataStream == null)
                {
                    await SendResponseAsync(425, "Can't open data connection");
                    return;
                }

                await SendResponseAsync(150, "Opening binary mode data connection for file transfer");

                using (dataStream)
                using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
                {
                    // Handle restart position
                    if (_restartPosition > 0)
                    {
                        fileStream.SetLength(_restartPosition);
                        fileStream.Seek(_restartPosition, SeekOrigin.Begin);
                        _restartPosition = 0;
                    }

                    await dataStream.CopyToAsync(fileStream);
                }

                await CloseDataConnectionAsync();
                await SendResponseAsync(226, "Transfer complete");
            }
            catch (Exception ex)
            {
                await CloseDataConnectionAsync();
                OnLogMessage($"STOR command error: {ex.Message}");
                await SendResponseAsync(550, $"Failed to store file: {ex.Message}");
            }
        }

        private async Task HandleAppeCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                var directory = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var dataStream = await OpenDataStreamAsync();
                if (dataStream == null)
                {
                    await SendResponseAsync(425, "Can't open data connection");
                    return;
                }

                await SendResponseAsync(150, "Opening binary mode data connection for file transfer");

                using (dataStream)
                using (var fileStream = new FileStream(fullPath, FileMode.Append, FileAccess.Write))
                {
                    await dataStream.CopyToAsync(fileStream);
                }

                await CloseDataConnectionAsync();
                await SendResponseAsync(226, "Transfer complete");
            }
            catch (Exception ex)
            {
                await CloseDataConnectionAsync();
                OnLogMessage($"APPE command error: {ex.Message}");
                await SendResponseAsync(550, $"Failed to append to file: {ex.Message}");
            }
        }

        private async Task HandleRestCommandAsync(string position)
        {
            if (!await CheckAuthenticationAsync())
                return;

            if (long.TryParse(position, out var pos))
            {
                _restartPosition = pos;
                await SendResponseAsync(350, $"Restart position accepted ({pos})");
            }
            else
            {
                await SendResponseAsync(501, "Invalid restart position");
            }
        }

        private async Task HandleSizeCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    await SendResponseAsync(213, fileInfo.Length.ToString());
                }
                else
                {
                    await SendResponseAsync(550, "File not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to get file size");
            }
        }

        private async Task HandleMdtmCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    var timestamp = fileInfo.LastWriteTimeUtc.ToString("yyyyMMddHHmmss");
                    await SendResponseAsync(213, timestamp);
                }
                else
                {
                    await SendResponseAsync(550, "File not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to get file modification time");
            }
        }

        private async Task HandleDeleCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    await SendResponseAsync(250, "File deleted");
                }
                else
                {
                    await SendResponseAsync(550, "File not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to delete file");
            }
        }

        private async Task HandleRmdCommandAsync(string dirname)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(dirname);
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                    await SendResponseAsync(250, "Directory deleted");
                }
                else
                {
                    await SendResponseAsync(550, "Directory not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to delete directory");
            }
        }

        private async Task HandleMkdCommandAsync(string dirname)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(dirname);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    var relativePath = fullPath.Substring(_rootDirectory.Length).Replace('\\', '/');
                    if (!relativePath.StartsWith("/"))
                        relativePath = "/" + relativePath;
                    await SendResponseAsync(257, $"\"{relativePath}\" directory created");
                }
                else
                {
                    await SendResponseAsync(550, "Directory already exists");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to create directory");
            }
        }

        private async Task HandleRnfrCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            try
            {
                var fullPath = ResolvePath(filename);
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    _renameFrom = fullPath;
                    await SendResponseAsync(350, "File or directory exists, ready for rename");
                }
                else
                {
                    await SendResponseAsync(550, "File or directory not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to prepare for rename");
            }
        }

        private async Task HandleRntoCommandAsync(string filename)
        {
            if (!await CheckAuthenticationAsync())
                return;

            if (string.IsNullOrEmpty(_renameFrom))
            {
                await SendResponseAsync(503, "RNFR required first");
                return;
            }

            try
            {
                var fullPath = ResolvePath(filename);
                var dir = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir!);
                }

                if (File.Exists(_renameFrom) || Directory.Exists(_renameFrom))
                {
                    File.Move(_renameFrom, fullPath);
                    _renameFrom = string.Empty;
                    await SendResponseAsync(250, "Rename successful");
                }
                else
                {
                    await SendResponseAsync(550, "Source file or directory not found");
                }
            }
            catch
            {
                await SendResponseAsync(550, "Failed to rename");
            }
        }

        private async Task HandleTypeCommandAsync(string type)
        {
            if (!await CheckAuthenticationAsync())
                return;

            if (type.ToUpper() == "I" || type.ToUpper() == "A")
            {
                await SendResponseAsync(200, $"Type set to {type}");
            }
            else
            {
                await SendResponseAsync(504, "Unsupported type");
            }
        }

        private async Task HandleSystCommandAsync()
        {
            if (!await CheckAuthenticationAsync())
                return;

            await SendResponseAsync(215, "UNIX Type: L8");
        }

        private async Task HandleNoopCommandAsync()
        {
            await SendResponseAsync(200, "OK");
        }

        private string ResolvePath(string path)
        {
            string absolutePath;

            if (path.StartsWith("/"))
            {
                // Absolute path
                absolutePath = Path.Combine(_rootDirectory, path.Substring(1).Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                // Relative path
                var current = _currentDirectory.Replace('/', Path.DirectorySeparatorChar);
                absolutePath = Path.Combine(_rootDirectory, current.TrimStart(Path.DirectorySeparatorChar), path.Replace('/', Path.DirectorySeparatorChar));
            }

            // Normalize the path
            absolutePath = Path.GetFullPath(absolutePath);

            // Security check - ensure path is within root directory
            if (!absolutePath.StartsWith(_rootDirectory))
            {
                throw new UnauthorizedAccessException("Access denied");
            }

            return absolutePath;
        }

        private string CombinePath(string basePath, string relativePath)
        {
            if (relativePath.StartsWith("/"))
                return relativePath;

            if (basePath == "/")
                return "/" + relativePath;

            return basePath + "/" + relativePath;
        }
        private string FormatListLine(string name, DateTime modified, FileAttributes attributes, long size)
        {
            // Format as per UNIX ls -l format with English locale
            var isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
            var perms = isDirectory ? "drwxrwxrwx" : "-rw-rw-rw-";
            var linkCount = "1";
            var owner = "owner";
            var group = "group";
            var sizeStr = isDirectory ? "0" : size.ToString();

            // 使用英文月份名称，确保兼容性
            string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun",
                        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            var month = months[modified.Month - 1];
            var day = modified.Day.ToString().PadLeft(2);

            // 判断是否为近期文件（6个月内）
            var now = DateTime.UtcNow;
            var isRecent = Math.Abs((now - modified.ToUniversalTime()).TotalDays) < 180;

            string timeField;
            if (isRecent)
            {
                // 近期文件显示时间 HH:mm
                timeField = modified.ToString("HH:mm");
            }
            else
            {
                // 较旧文件显示年份
                timeField = modified.ToString("yyyy");
            }

            // 标准Unix ls格式输出，确保字段对齐
            return $"{perms} {linkCount,3} {owner,-8} {group,-8} {sizeStr,12} {month,3} {day,2} {timeField,5} {name}";
        }
        private async Task<bool> CheckAuthenticationAsync()
        {
            if (!_authenticated)
            {
                await SendResponseAsync(530, "Not logged in");
                return false;
            }
            return true;
        }

        private async Task<NetworkStream?> OpenDataStreamAsync()
        {
            try
            {
                if (_passiveMode && _dataListener != null)
                {
                    // Passive mode - wait for client to connect
                    OnLogMessage("Waiting for passive connection...");
                    _dataClient = await _dataListener.AcceptTcpClientAsync();
                    _dataListener.Stop();
                    _dataListener = null;
                    OnLogMessage("Passive connection established");
                }
                else if (!_passiveMode && _passiveEndpoint != null)
                {
                    // Active mode - connect to client
                    OnLogMessage($"Connecting to client at {_passiveEndpoint.Address}:{_passiveEndpoint.Port}");
                    _dataClient = new TcpClient();
                    await _dataClient.ConnectAsync(_passiveEndpoint.Address, _passiveEndpoint.Port);
                    OnLogMessage("Active connection established");
                }
                else
                {
                    OnLogMessage("No data connection endpoint configured");
                    return null;
                }

                if (_dataClient != null && _dataClient.Connected)
                {
                    return _dataClient.GetStream();
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"Data connection error: {ex.Message}");
            }

            return null;
        }

        private async Task CloseDataConnectionAsync()
        {
            if (_dataClient != null)
            {
                _dataClient.Close();
                _dataClient = null;
            }

            if (_dataListener != null)
            {
                _dataListener.Stop();
                _dataListener = null;
            }

            _passiveEndpoint = null;
            _passiveMode = false;
        }

        private async Task SendResponseAsync(int code, string message)
        {
            try
            {
                // 确保响应格式正确
                var response = $"{code} {message}\r\n";
                OnLogMessage($"Sending response: {code} {message}");

                await _controlWriter.WriteAsync(response);
                await _controlWriter.FlushAsync();

                OnLogMessage($"Response sent successfully: {code} {message}");
            }
            catch (Exception ex)
            {
                OnLogMessage($"Error sending response {code} {message}: {ex.Message}");
                throw;
            }
        }

        public async Task CloseAsync()
        {
            OnLogMessage("Closing client session");

            try
            {
                await CloseDataConnectionAsync();
            }
            catch (Exception ex)
            {
                OnLogMessage($"Error closing data connection: {ex.Message}");
            }

            try
            {
                if (_controlWriter != null)
                {
                    await _controlWriter.FlushAsync();
                }
            }
            catch { /* Ignore */ }

            try
            {
                if (_controlClient?.Connected == true)
                {
                    _controlClient?.Close();
                }
            }
            catch { /* Ignore */ }

            OnLogMessage("Session closed");
        }

        protected virtual void OnLogMessage(string message)
        {
            LogMessage?.Invoke(this, message);
        }

        public void Dispose()
        {
            OnLogMessage("Disposing session");

            try
            {
                CloseDataConnectionAsync().Wait(1000);
            }
            catch { /* Ignore */ }

            try
            {
                _controlReader?.Dispose();
            }
            catch { /* Ignore */ }

            try
            {
                _controlWriter?.Dispose();
            }
            catch { /* Ignore */ }

            try
            {
                _controlStream?.Dispose();
            }
            catch { /* Ignore */ }

            try
            {
                if (_controlClient?.Connected == true)
                {
                    _controlClient?.Close();
                }
            }
            catch { /* Ignore */ }

            OnLogMessage("Session disposed");
        }
    }
}