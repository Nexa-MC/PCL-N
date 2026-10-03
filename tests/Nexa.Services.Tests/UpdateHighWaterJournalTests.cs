using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void UpdateJournalRejectsRollbackAndCorruption()
    {
        using var state = new MemoryStream();
        UpdateHighWaterJournal.Append(state, "2.0.0.alpha.5");
        AssertEqual("2.0.0.alpha.5", UpdateHighWaterJournal.Read(state).Version);
        byte[] committed = state.ToArray();
        AssertThrows<InvalidOperationException>(() => UpdateHighWaterJournal.Append(state, "2.0.0.alpha.5"));
        AssertThrows<InvalidOperationException>(() => UpdateHighWaterJournal.Append(state, "1.4.14"));
        AssertTrue(state.ToArray().SequenceEqual(committed));
        foreach (int index in new[] { 0, 4, 8, committed.Length - 5, committed.Length - 1 })
        {
            byte[] corrupt = committed.ToArray();
            corrupt[index] ^= 0xff;
            using var input = new MemoryStream(corrupt);
            AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Read(input));
            AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Append(input, "2.0.0.alpha.6"));
            AssertTrue(input.ToArray().SequenceEqual(corrupt));
        }
        using var duplicated = new MemoryStream();
        duplicated.Write(committed);
        duplicated.Write(committed);
        AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Read(duplicated));
        using var tooLarge = new MemoryStream(new byte[UpdateHighWaterJournal.MaximumBytes + 1]);
        AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Read(tooLarge));
        foreach (string invalid in new[] { "2.0.0.ci.abcdef", "2.0.0.alpha.0", "2.0", "2.0.0.alpha.05" })
            AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Encode(invalid));
    }

    private static void UpdateJournalRecoversEveryInterruptedAppend()
    {
        byte[] first = UpdateHighWaterJournal.Encode("2.0.0.alpha.5");
        byte[] next = UpdateHighWaterJournal.Encode("2.0.0.alpha.6");
        for (int cut = 0; cut < next.Length; cut++)
        {
            using var interrupted = new MemoryStream();
            interrupted.Write(first);
            interrupted.Write(next.AsSpan(0, cut));
            AssertEqual("2.0.0.alpha.5", UpdateHighWaterJournal.Read(interrupted).Version);
            UpdateHighWaterJournal.Append(interrupted, "2.0.0.alpha.7");
            AssertEqual("2.0.0.alpha.7", UpdateHighWaterJournal.Read(interrupted).Version);
            AssertTrue(interrupted.ToArray().AsSpan(0, first.Length).SequenceEqual(first));
            AssertEqual(first.Length + UpdateHighWaterJournal.Encode("2.0.0.alpha.7").Length, (int)interrupted.Length);
        }
        using var garbage = new MemoryStream();
        garbage.Write(first);
        garbage.Write("bad"u8);
        AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Read(garbage));
        using var invalidPrefix = new MemoryStream("NXH1\xff"u8.ToArray());
        AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Read(invalidPrefix));
    }

    private static void UpdateJournalCapacityNeverDiscardsCommittedRecords()
    {
        using var full = new MemoryStream();
        int sequence = 1;
        while (true)
        {
            byte[] record = UpdateHighWaterJournal.Encode($"2.0.0.alpha.{sequence}");
            if (full.Length + record.Length > UpdateHighWaterJournal.MaximumBytes) break;
            full.Write(record);
            sequence++;
        }
        byte[] before = full.ToArray();
        AssertThrows<InvalidDataException>(() => UpdateHighWaterJournal.Append(full, $"2.0.0.alpha.{sequence}"));
        AssertTrue(before.SequenceEqual(full.ToArray()));
        AssertEqual($"2.0.0.alpha.{sequence - 1}", UpdateHighWaterJournal.Read(full).Version);
    }
}
