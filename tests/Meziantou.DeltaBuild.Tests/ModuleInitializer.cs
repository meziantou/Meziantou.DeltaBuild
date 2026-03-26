using System.Runtime.CompilerServices;
using Meziantou.Framework.InlineSnapshotTesting;

namespace Meziantou.DeltaBuild.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        InlineSnapshotSettings.Default = InlineSnapshotSettings.Default with
        {
            AutoDetectContinuousEnvironment = true,
            ForceUpdateSnapshots = false,
        };
    }
}
