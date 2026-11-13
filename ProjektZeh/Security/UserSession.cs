namespace ProjektZeh.Security;

internal sealed class UserSession
{
    public string? UserId { get; private set; }
    public string? UserName { get; private set; }
    public bool IsAdministrator { get; private set; }
    public string? HomeDirectory { get; private set; }

    public UserSession() { }

    internal UserSession(string userId, string userName, bool administrator, string homeDirectory) =>
        Login(userId, userName, administrator, homeDirectory);

    internal void Login(string userId, string userName, bool administrator, string homeDirectory)
    {
        UserId = userId;
        UserName = userName;
        IsAdministrator = administrator;
        HomeDirectory = homeDirectory;
    }

    public void Logout()
    {
        UserId = UserName = HomeDirectory = null;
        IsAdministrator = false;
    }
}
