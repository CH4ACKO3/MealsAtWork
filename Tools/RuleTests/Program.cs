using DeskLunch;

int checks = 0;
foreach (int duration in Enumerable.Range(1, 1000))
foreach (int batch in new[] { 1, 3, 10, 60, 150, 250 })
{
    int elapsed = 0, work = 0, remainder = 0, total = duration + 300;
    while (elapsed < total)
    {
        int delta = Math.Min(batch, total - elapsed);
        int effective = LunchTiming.WorkTicks(delta, duration - elapsed, ref remainder);
        if (effective < 0 || effective > delta) throw new Exception("Invalid work tick budget");
        work += effective; elapsed += delta; checks++;
    }
    if (work != duration * 4 / 5 + 300) throw new Exception("Batch-dependent work speed");
    checks++;
}
Console.WriteLine($"PASS: {checks} production work-timing checks; full engine tests require installed RimWorld.");
