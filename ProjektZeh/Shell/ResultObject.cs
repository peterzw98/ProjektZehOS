namespace ProjektZeh.Shell;

/// <summary>
/// Ergebnis einer Operation mit Rueckgabewert.
/// </summary>
/// <typeparam name="T"></typeparam>
internal sealed class ResultObject<T> : ResultErr
{
    public T Value { get; }

    public ResultObject(T value, EnmErrCode errorCode = EnmErrCode.None, string errorMessage = "")
        : base(errorCode, errorMessage)
    {
        Value = value;
    }
}
