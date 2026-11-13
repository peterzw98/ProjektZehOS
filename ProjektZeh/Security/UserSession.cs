namespace ProjektZeh.Security;

internal sealed class UserSession
{
    public string? UserId { get; private set; }
    public string? UserName { get; private set; }
    public bool IsAdministrator { get; private set; }
    public string? HomeDirectory { get; private set; }
    /// <summary>Aendert sich bei jedem An- und Abmelden, damit Oberflaechen Sitzungsdaten verwerfen koennen.</summary>
    public int Generation { get; private set; }

    public UserSession() { }

    internal UserSession(string userId, string userName, bool administrator, string homeDirectory) =>
        Login(userId, userName, administrator, homeDirectory);

    internal void Login(string userId, string userName, bool administrator, string homeDirectory)
    {
        UserId = userId;
        UserName = userName;
        IsAdministrator = administrator;
        HomeDirectory = homeDirectory;
        Generation++;
    }

    /// <summary>Uebernimmt die Identitaet einer anderen Sitzung, etwa fuer ein neues Terminal des angemeldeten Desktops.</summary>
    internal void LoginAs(UserSession other)
    {
        if (other.UserId != null) Login(other.UserId, other.UserName!, other.IsAdministrator, other.HomeDirectory!);
    }

    public void Logout()
    {
        if (UserId == null) return;
        UserId = UserName = HomeDirectory = null;
        IsAdministrator = false;
        Generation++;
    }
}
