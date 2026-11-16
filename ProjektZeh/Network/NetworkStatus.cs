namespace ProjektZeh.Network;

internal readonly record struct NetworkStatus(bool Adapter, bool Link, string? Address)
{
    public string Text => !Adapter ? "Kein Netzwerkadapter" : !Link ? "Keine Verbindung" :
        Address == null ? "Suche nach Netzwerk ..." : "Verbunden";
}
