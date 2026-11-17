using System;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>Fuehrt den vorhandenen Migrationsablauf fuer eine gepruefte Zielplatte aus.</summary>
internal interface IMigrationService
{
    ResultObject<MigrationReport> Run(string diskName, Action<string> log, CancellationToken cancellation);
}
