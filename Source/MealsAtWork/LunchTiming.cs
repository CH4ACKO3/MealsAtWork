using System;

namespace DeskLunch
{
    // Integer fifths keep the 80% rate exact even when the game ticks in batches.
    public static class LunchTiming
    {
        public static int WorkTicks(int delta, int eatingTicks, ref int remainder)
        {
            int eating = Math.Min(Math.Max(eatingTicks, 0), delta);
            int fifths = eating * 4 + remainder;
            remainder = fifths % 5;
            return delta - eating + fifths / 5;
        }
    }
}
