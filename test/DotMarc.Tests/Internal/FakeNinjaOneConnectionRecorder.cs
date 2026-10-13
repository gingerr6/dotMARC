using DotMarc.Psa.NinjaOne;

namespace DotMarc.Tests.Internal;

internal sealed class FakeNinjaOneConnectionRecorder : INinjaOneConnectionRecorder
{
    public List<string> LostReasons { get; } = [];
    public int Refreshes { get; private set; }

    public Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default)
    {
        LostReasons.Add(reason);
        return Task.CompletedTask;
    }

    public Task RefreshedAsync(CancellationToken cancellationToken = default)
    {
        Refreshes++;
        return Task.CompletedTask;
    }
}
