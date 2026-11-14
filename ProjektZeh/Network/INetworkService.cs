using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

internal interface INetworkService
{
    ResultObject<string> Execute(string operation, string[] arguments, CancellationToken cancellation);
}
