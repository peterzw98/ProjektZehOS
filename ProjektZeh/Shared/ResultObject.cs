namespace ProjektZeh.Shared;

/// <summary>
/// Verbindet einen Operationsstatus mit einem optionalen Ergebniswert.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class ResultObject<T>
{
    public ResultErr Status { get; }
    public T? Value { get; }

    public ResultObject() : this(new ResultErr(), default)
    {
    }

    internal ResultObject(ResultErr status, T? value)
    {
        Status = status;
        Value = value;
    }

    /// <summary>
    /// Prueft den gemeinsamen Operationsstatus.
    /// </summary>
    /// <returns></returns>
    public bool IsOK()
    {
        return Status.IsOK();
    }

    /// <summary>
    /// Gibt die Diagnose des Operationsstatus zurueck.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return Status.ToString();
    }
}
