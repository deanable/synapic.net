using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Tests that reset the process-global SynapicLog (Initialize/ResetForTests)
/// must not run in parallel with each other — xUnit parallelizes across
/// collections by default, which would corrupt each other's log files.
/// </summary>
[CollectionDefinition("SynapicLogSerial")]
public sealed class SynapicLogSerialCollection
{
}
