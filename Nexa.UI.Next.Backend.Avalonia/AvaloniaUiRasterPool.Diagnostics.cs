namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiRasterPool
{
    private AvaloniaUiMemoryObservation _lastPressure = new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable");
    internal (long Bytes, long AdmissionBudget, int Entries, int Leases, long DecodeAttempts,
        long DisposedBitmaps, AvaloniaUiMemoryObservation Pressure) CaptureDiagnostics()
    {
        lock (_gate)
        {
            int leases = 0;
            foreach (Entry entry in _entries.Values) leases += entry.References;
            return (_bytes, _admissionBudget, _entries.Count, leases, _decodeAttempts, _disposedBitmaps, _lastPressure);
        }
    }
}
