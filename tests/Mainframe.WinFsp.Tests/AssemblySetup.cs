using System.Runtime.CompilerServices;
using Mainframe.WinFsp;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Mainframe.WinFsp.Tests;

internal static class AssemblySetup
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => WinFspRuntime.Initialize();
#pragma warning restore CA2255
}
