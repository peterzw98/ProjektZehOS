namespace ProjektZeh.Messaging;

internal sealed class OrderedTransfer
{
    private readonly object firstGate = new();
    private readonly object secondGate = new();
    private int first = 1000;
    private int second = 1000;

    public void Transfer(bool toSecond)
    {
        // Beide Richtungen sperren zuerst Konto 1, dann Konto 2; die fachliche Richtung ändert diese Reihenfolge nicht.
        lock (firstGate)
        lock (secondGate)
        {
            int amount = toSecond ? 1 : -1;
            first -= amount;
            second += amount;
        }
    }

    public int Total
    {
        get
        {
            lock (firstGate)
            lock (secondGate) return first + second;
        }
    }
}
