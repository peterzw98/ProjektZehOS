namespace ProjektZeh.Shared;

/// <summary>
/// Gemeinsame Ergebniszustaende der ZehOS-Bausteine.
/// </summary>
public enum ResultCode
{
    Uninitialized,
    Success,
    InvalidArgument,
    MissingArgument,
    NotFound,
    Failure,
    Exception
}
