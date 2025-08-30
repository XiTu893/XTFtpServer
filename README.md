```markdown
# XTFtpServer

![XTFtpServer Logo](https://github.com/XiTu893/XTFtpServer/blob/main/XT-Logo.png)  
基于 C# 的轻量级、高性能 FTP 服务器库，支持快速集成到 .NET 应用，无需深入理解 FTP 协议细节，几行代码即可实现完整的文件传输服务。


## 📋 特性

| 功能 | 描述 |
|------|------|
| **完整 FTP 协议支持** | 兼容主动模式（PORT）、被动模式（PASV），支持文件上传（STOR）、下载（RETR）、目录操作（MKD/RMD/LIST）等核心命令 |
| **灵活的认证体系** | 内置用户密码认证，支持自定义扩展（如对接数据库、LDAP），基于用户的权限控制（只读/读写）和目录隔离 |
| **异步高性能** | 基于 .NET 异步 IO 模型，非阻塞处理客户端连接，单服务器可稳定支持数百个并发连接 |
| **详细日志监控** | 多级别日志（Debug/Info/Warn/Error），记录客户端连接、文件操作、错误信息，支持事件扩展集成监控系统 |
| **跨框架兼容** | 支持 .NET 6+ / .NET 7+ / .NET 8+ 和 .NET Framework 4.8+，无缝集成到传统桌面应用和现代跨平台项目 |
| **高度可扩展** | 提供自定义文件系统（如对接云存储）、自定义命令处理、自定义日志输出等扩展接口，满足复杂业务需求 |


## 🚀 快速开始

### 1. 环境要求
- .NET 6+ 或 .NET Framework 4.8+
- Windows 7+ / Windows Server 2012+（Linux/macOS 支持实验性）
- Visual Studio 2022+ 或 Rider 2022+


### 2. 安装方式

#### 方式 1：NuGet 安装（推荐）
通过 .NET CLI 或 NuGet 包管理器安装：
```bash
# .NET CLI
dotnet add package XTFtpServer

# Package Manager Console
Install-Package XTFtpServer
```

#### 方式 2：源码编译
克隆仓库并手动编译：
```bash
# 克隆源码
git clone https://github.com/XiTu893/XTFtpServer.git

# 进入项目目录
cd XTFtpServer

# 编译项目
dotnet build
```


### 3. 入门示例
以下代码演示如何在 30 秒内启动一个 FTP 服务器：

```csharp
using System;
using System.Threading.Tasks;
using XTFtpServer;
using XTFtpServer.Authentication;

namespace XTFtpDemo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. 创建用户认证服务（支持多用户+权限控制）
            var authService = new FtpUserAuthenticationService();
            // 添加普通用户（只读权限）
            authService.AddUser(
                username: "testuser", 
                password: "Test@123", 
                rootPath: @"D:\FTP\test", 
                permissions: FtpUserPermissions.ReadOnly
            );
            // 添加管理员用户（读写权限）
            authService.AddUser(
                username: "admin", 
                password: "Admin@123", 
                rootPath: @"D:\FTP\admin", 
                permissions: FtpUserPermissions.ReadWrite
            );

            // 2. 配置服务器选项
            var serverOptions = new FtpServerOptions
            {
                Port = 21,                  // FTP 标准端口
                MaxConcurrentConnections = 50, // 最大并发连接数
                EnablePassiveMode = true,   // 启用被动模式（推荐）
                PassivePortRange = new Range(50000, 50100), // 被动模式端口范围
                LogLevel = FtpLogLevel.Info // 日志级别
            };

            // 3. 创建并启动服务器
            using var ftpServer = new XTFtpServer(authService, serverOptions);
            
            // 注册日志事件（可选）
            ftpServer.LogReceived += (sender, e) => 
                Console.WriteLine($"[{e.LogLevel}] {DateTime.Now:HH:mm:ss} - {e.Message}");

            Console.WriteLine("正在启动 XTFtpServer...");
            await ftpServer.StartAsync();
            Console.WriteLine($"服务器已启动，监听端口: {serverOptions.Port}");
            Console.WriteLine("按任意键停止服务器...\n");

            // 等待用户输入停止
            Console.ReadKey();
            await ftpServer.StopAsync();
            Console.WriteLine("服务器已停止");
        }
    }
}
```


## 📖 详细文档

| 文档主题 | 链接 |
|----------|------|
| API 参考 | [API Documentation](https://github.com/XiTu893/XTFtpServer/wiki/API-Reference) |
| 配置指南 | [Server Configuration](https://github.com/XiTu893/XTFtpServer/wiki/Server-Configuration) |
| 自定义认证 | [Custom Authentication](https://github.com/XiTu893/XTFtpServer/wiki/Custom-Authentication) |
| 扩展文件系统 | [Extend File System](https://github.com/XiTu893/XTFtpServer/wiki/Extend-File-System) |
| 常见问题 | [FAQ](https://github.com/XiTu893/XTFtpServer/wiki/FAQ) |


## 📁 示例项目

仓库 `samples` 目录包含多种场景的示例代码，可直接运行：

| 示例名称 | 描述 |
|----------|------|
| `DesktopFtpApp` | Windows 桌面应用集成 FTP 服务，支持图形化配置 |
| `WindowsServiceFtp` | 将 FTP 服务封装为 Windows 服务，支持开机自启 |
| `WebFtpIntegration` | ASP.NET Core 项目中集成 FTP 服务，实现 Web 管理界面 |
| `CustomFileSystemDemo` | 自定义文件系统示例（对接本地文件+阿里云 OSS） |


## 🔧 进阶用法

### 自定义用户认证（对接数据库）
```csharp
// 实现自定义认证服务（继承 IFtpAuthenticationService）
public class DatabaseFtpAuthService : IFtpAuthenticationService
{
    private readonly IDbConnection _dbConnection;

    public DatabaseFtpAuthService(IDbConnection dbConnection)
    {
        _dbConnection = dbConnection;
    }

    // 从数据库验证用户
    public async Task<FtpUser> AuthenticateAsync(string username, string password)
    {
        var sql = "SELECT Username, PasswordHash, RootPath, Permissions FROM FtpUsers WHERE Username = @Username";
        var user = await _dbConnection.QueryFirstOrDefaultAsync<FtpUserDto>(sql, new { Username = username });
        
        if (user == null || !BCrypt.Verify(password, user.PasswordHash))
            return null; // 认证失败
        
        // 返回 FTP 用户信息
        return new FtpUser(
            username: user.Username,
            rootPath: user.RootPath,
            permissions: (FtpUserPermissions)user.Permissions
        );
    }
}

// 使用自定义认证服务
var dbAuthService = new DatabaseFtpAuthService(new SqlConnection("your-connection-string"));
var ftpServer = new XTFtpServer(dbAuthService, serverOptions);
```

### 监听客户端连接/断开事件
```csharp
// 客户端连接事件
ftpServer.ClientConnected += (sender, e) => 
{
    Console.WriteLine($"[新连接] IP: {e.ClientIp}, 端口: {e.ClientPort}, 时间: {DateTime.Now}");
    // 可在此处记录连接日志或限制特定 IP
};

// 客户端断开事件
ftpServer.ClientDisconnected += (sender, e) => 
{
    Console.WriteLine($"[断开连接] IP: {e.ClientIp}, 端口: {e.ClientPort}, 传输文件数: {e.FileTransferCount}");
};
```


## ❗ 注意事项

1. **端口占用**：确保 FTP 端口（默认 21）和被动模式端口范围（默认 50000-50100）未被其他程序占用，且防火墙已开放这些端口。
2. **权限控制**：运行 FTP 服务的账户需拥有根目录的读写权限（如 `D:\FTP`），建议使用非管理员账户运行以降低安全风险。
3. **日志存储**：生产环境建议将日志写入文件（如 Serilog + Elasticsearch），而非仅控制台输出。
4. **跨平台支持**：Linux/macOS 下需安装 `libgdiplus`（用于日志格式化），且被动模式需配置端口映射。


## 🤝 贡献指南

欢迎通过以下方式参与项目贡献：

1. **提交 Bug 或需求**：在 [Issues](https://github.com/XiTu893/XTFtpServer/issues) 中提交详细描述
2. **代码贡献**：
   - Fork 仓库
   - 创建特性分支（`git checkout -b feature/your-feature`）
   - 提交代码（`git commit -m "feat: 添加XX功能"`）
   - 推送分支（`git push origin feature/your-feature`）
   - 发起 Pull Request
3. **文档完善**：修改 Wiki 或 README，补充使用案例或教程


## 📄 许可证

本项目基于 **MIT 许可证** 开源，可免费用于商业和个人项目。详见 [LICENSE](LICENSE) 文件。


## 💖 支持作者

XTFtpServer 是开源免费项目，开发和维护需要大量时间和精力。如果这个库对您有帮助，欢迎通过以下方式支持作者：

| 支付方式 | 二维码 |
|----------|--------|
| 微信赞赏 | ![微信赞赏码](QrReward) |
| 支付宝赞赏 | ![支付宝赞赏码](https://github.com/XiTu893/XTFtpServer/blob/main/QrReward.jpg) |

其他支持方式：
- 给本仓库点个 ⭐️ Star
- 分享给身边的开发者
- 在技术社区（如 Stack Overflow、掘金）推荐本项目


## 📞 联系作者

- GitHub: [溪土工作室](https://github.com/XiTu893)
- Email: 28491599@qq.com 


 