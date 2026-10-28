namespace ProjektZeh.Shell;

/// <summary>
/// Ergebnis einer Operation ohne Rueckgabewert.
/// </summary>
internal class ResultErr
{
    public EnmErrCode eResult { get; }
    public string sErrMsg { get; }

    public ResultErr(EnmErrCode errorCode = EnmErrCode.None, string errorMessage = "")
    {
        eResult = errorCode;
        sErrMsg = errorMessage;
    }

    /// <summary>
    /// Prueft, ob die Operation erfolgreich war.
    /// </summary>
    /// <returns></returns>
    public bool IsOK()
    {
        return eResult switch
        {
            EnmErrCode.None => true,
            _ => false
        };
    }
}
