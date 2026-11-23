using System;
using System.Collections.Generic;

namespace Rumbo.Ar.Domain
{
    public sealed class VioHistory
    {
        readonly List<VioSample> samples = new();
        public void Clear() => samples.Clear();
        public void Add(VioSample sample)
        {
            if (!NavigationSession.Finite(sample.Position.Length) || sample.CapturedAtUnixMs <= 0) return;
            if (samples.Count > 0 && sample.CapturedAtUnixMs <= samples[samples.Count - 1].CapturedAtUnixMs) return;
            samples.Add(sample);
            while (samples.Count > 512 || sample.CapturedAtUnixMs - samples[0].CapturedAtUnixMs > 5000) samples.RemoveAt(0);
        }

        public bool TryAt(long timestamp, long maximumSkewMs, out VioSample sample)
        {
            sample = default;
            if (samples.Count == 0 || timestamp < samples[0].CapturedAtUnixMs || timestamp > samples[samples.Count - 1].CapturedAtUnixMs) return false;
            for (var i = 0; i < samples.Count; i++)
            {
                var after = samples[i];
                if (after.CapturedAtUnixMs < timestamp) continue;
                if (after.CapturedAtUnixMs == timestamp) { sample = after; return true; }
                if (i == 0) return false;
                var before = samples[i - 1];
                if (timestamp - before.CapturedAtUnixMs > maximumSkewMs || after.CapturedAtUnixMs - timestamp > maximumSkewMs) return false;
                var t = (double)(timestamp - before.CapturedAtUnixMs) / (after.CapturedAtUnixMs - before.CapturedAtUnixMs);
                sample = new VioSample(timestamp, before.Position + (after.Position - before.Position) * t);
                return true;
            }
            return false;
        }
    }
}
