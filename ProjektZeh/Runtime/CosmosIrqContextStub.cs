namespace Cosmos.Kernel.Core.CPU;

/// <summary>
/// Attrappe fuer den Plug auf <c>Panic.PrintCpuContext(ref IRQContext)</c> (<see cref="ProjektZeh.Runtime.CosmosPanicHaltPlug"/>):
/// Das echte Struct ist in Cosmos.Kernel.Core intern, Cosmos.Patcher vergleicht Parametertypen aber nur ueber den vollen
/// Namen. Die Felder liest ZehOS ausschliesslich ueber den Zeiger auf den Rahmen.
/// </summary>
public struct IRQContext
{
}
