using System;

namespace Kalo.Spike0
{
    /// <summary>A controlled stop: the message is shown to the user as-is.</summary>
    internal sealed class SpikeAbort : Exception
    {
        public SpikeAbort(string message) : base(message) { }
    }
}
