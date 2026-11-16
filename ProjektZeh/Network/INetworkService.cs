using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

internal interface INetworkService
{
    NetworkStatus Status { get; }
    ResultObject<string> Execute(string operation, string[] arguments, CancellationToken cancellation);
}
