using System;

namespace ProjektZeh.Shared;

/// <summary>
/// Erstellt Wertergebnisse mit einem eindeutig gesetzten Operationsstatus.
/// </summary>
public static class ResultObjectFactory
{
    /// <summary>
    /// Erstellt ein erfolgreiches Wertergebnis.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="value"></param>
    /// <returns></returns>
    public static ResultObject<T> CreateSuccess<T>(T? value)
    {
        return new ResultObject<T>(ResultErr.CreateSuccess(), value);
    }

    /// <summary>
    /// Erstellt ein fehlgeschlagenes Ergebnis ohne Nutzdaten.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="code"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ResultObject<T> CreateFailure<T>(ResultCode code, string? message)
    {
        return new ResultObject<T>(ResultErr.CreateFailure(code, message), default);
    }

    /// <summary>
    /// Reicht einen bestehenden Fehlerstatus ohne Informationsverlust weiter.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="status"></param>
    /// <returns></returns>
    public static ResultObject<T> CreateFailure<T>(ResultErr? status)
    {
        if (status is null)
        {
            return CreateFailure<T>(ResultCode.MissingArgument, "Der Fehlerstatus fehlt.");
        }

        if (status.IsOK())
        {
            return CreateFailure<T>(ResultCode.InvalidArgument,
                "Ein Erfolgsstatus kann nicht als Fehler weitergereicht werden.");
        }

        return new ResultObject<T>(status, default);
    }

    /// <summary>
    /// Erstellt ein Wertergebnis aus den Details einer Exception.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="exception"></param>
    /// <returns></returns>
    public static ResultObject<T> CreateFromException<T>(Exception? exception)
    {
        return CreateFailure<T>(ResultErr.CreateFromException(exception));
    }
}
