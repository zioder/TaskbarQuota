using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskbarQuota.ViewModels
{
    internal readonly record struct SpendRingArc(double Start, double End);

    internal static class SpendRingLayout
    {
        private const double MinimumSliceShare = 0.025;

        public static IReadOnlyList<SpendRingArc> BuildArcs(IReadOnlyList<double> values)
        {
            var positiveTotal = values.Where(value => value > 0).Sum();
            if (positiveTotal <= 0)
                return Array.Empty<SpendRingArc>();

            var shares = values
                .Where(value => value > 0)
                .Select(value => Math.Max(value / positiveTotal, MinimumSliceShare))
                .ToArray();
            var adjustedTotal = shares.Sum();
            var arcs = new List<SpendRingArc>(shares.Length);
            double cursor = 0;
            foreach (var share in shares)
            {
                var width = share / adjustedTotal;
                arcs.Add(new SpendRingArc(cursor, cursor + width));
                cursor += width;
            }
            return arcs;
        }
    }
}
