using System;
using System.Text;

namespace ProjektZeh.Shared;

/// <summary>
/// Unveraenderlicher Status einer Operation ohne Nutzdaten.
/// </summary>
public sealed class ResultErr
{
    public ResultCode Code { get; }
    public string Message { get; }
    public string? ExceptionDetails { get; }

    public ResultErr() : this(ResultCode.Uninitialized, null, null)
    {
    }

    private ResultErr(ResultCode code, string? message, string? exceptionDetails)
    {
        Code = code;
        Message = message ?? string.Empty;
        ExceptionDetails = exceptionDetails;
    }

    /// <summary>
    /// Erstellt einen ausdruecklichen Erfolgsstatus.
    /// </summary>
    /// <returns></returns>
    public static ResultErr CreateSuccess()
    {
        return new ResultErr(ResultCode.Success, null, null);
    }

    /// <summary>
    /// Erstellt einen Fehlerstatus mit der Ursache der Operation.
    /// </summary>
    /// <param name="code"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ResultErr CreateFailure(ResultCode code, string? message)
    {
        switch (code)
        {
            case ResultCode.Success:
                return new ResultErr(ResultCode.InvalidArgument,
                    "Ein Fehlerergebnis benoetigt einen Fehlercode.", null);
            default:
                return new ResultErr(code, message, null);
        }
    }

    /// <summary>
    /// Uebernimmt die Diagnose einer fehlgeschlagenen Operation.
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static ResultErr CreateFromException(Exception? exception)
    {
        if (exception is null)
        {
            return CreateFailure(ResultCode.MissingArgument, "Die Exception fehlt.");
        }

        return new ResultErr(ResultCode.Exception, exception.Message, exception.ToString());
    }

    /// <summary>
    /// Prueft, ob die Operation erfolgreich abgeschlossen wurde.
    /// </summary>
    /// <returns></returns>
    public bool IsOK()
    {
        return Code switch
        {
            ResultCode.Success => true,
            _ => false
        };
    }

    /// <summary>
    /// Erstellt eine lesbare Diagnose aus Status und Fehlerdetails.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        var diagnostic = new StringBuilder(Code.ToString());
        if (Message.Length > 0)
        {
            diagnostic.Append(": ").Append(Message);
        }

        if (!string.IsNullOrEmpty(ExceptionDetails))
        {
            diagnostic.AppendLine().Append(ExceptionDetails);
        }

        return diagnostic.ToString();
    }
}
