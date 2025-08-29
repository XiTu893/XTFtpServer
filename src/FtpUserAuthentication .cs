using System.Collections.Concurrent;

namespace XTFtpServer
{
    public interface IFtpUserAuthentication
    {
        bool AuthenticateUser(string username, string password);
    }

    public class FtpUserAuthentication : IFtpUserAuthentication
    {
        private readonly ConcurrentDictionary<string, string> _users;

        public FtpUserAuthentication()
        {
            _users = new ConcurrentDictionary<string, string>();
        }

        public void AddUser(string username, string password)
        {
            _users[username] = password;
        }

        public bool RemoveUser(string username)
        {
            return _users.TryRemove(username, out _);
        }

        public bool AuthenticateUser(string username, string password)
        {
            return _users.TryGetValue(username, out var storedPassword) && storedPassword == password;
        }
    }
}