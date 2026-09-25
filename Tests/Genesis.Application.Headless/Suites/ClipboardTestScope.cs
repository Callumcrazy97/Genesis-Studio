using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Headless.Suites;

/// <summary>Preserves the user's clipboard around the few regressions that must exercise Win32 paste.</summary>
internal sealed class ClipboardTestScope : IDisposable
{
    private readonly IDataObject? _snapshot;
    private readonly List<IDisposable> _ownedCopies;
    private readonly bool _restore;
    private bool _disposed;

    private ClipboardTestScope(
        IDataObject? snapshot,
        List<IDisposable> ownedCopies,
        bool restore)
    {
        _snapshot = snapshot;
        _ownedCopies = ownedCopies;
        _restore = restore;
    }

    public static ClipboardTestScope Capture()
    {
        List<IDisposable> owned = [];
        try
        {
            IDataObject? source = Clipboard.GetDataObject();
            if (source is null)
            {
                return new ClipboardTestScope(null, owned, restore: true);
            }

            DataObject snapshot = new();
            foreach (string format in source.GetFormats(autoConvert: false))
            {
                try
                {
                    object? value = source.GetData(format, autoConvert: false);
                    value = value switch
                    {
                        Bitmap bitmap => Own(new Bitmap(bitmap), owned),
                        MemoryStream stream => Own(
                            new MemoryStream(stream.ToArray(), writable: false),
                            owned),
                        string[] paths => paths.ToArray(),
                        _ => value,
                    };
                    if (value is not null)
                    {
                        snapshot.SetData(format, autoConvert: false, value);
                    }
                }
                catch (Exception exception) when (
                    exception is ExternalException or ArgumentException or NotSupportedException)
                {
                    // Clipboard owners may advertise delayed/private formats which cannot be
                    // materialised. Preserve every stable format and ignore only that one.
                }
            }

            return new ClipboardTestScope(
                snapshot.GetFormats(autoConvert: false).Length == 0 ? null : snapshot,
                owned,
                restore: snapshot.GetFormats(autoConvert: false).Length > 0);
        }
        catch (ExternalException)
        {
            // A transient owner prevented a safe snapshot. Do not later overwrite clipboard data
            // we could not preserve; the set/read retry still decides whether the case can run.
            return new ClipboardTestScope(null, owned, restore: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int attempt = 0; _restore && attempt < 12; attempt++)
        {
            try
            {
                if (_snapshot is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(_snapshot, copy: true);
                }

                break;
            }
            catch (ExternalException exception)
            {
                if (attempt == 11)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Could not restore the pre-test clipboard after bounded retries: {exception.Message}");
                    break;
                }

                Thread.Sleep(40 + (attempt * 20));
            }
        }

        foreach (IDisposable owned in _ownedCopies)
        {
            owned.Dispose();
        }
    }

    private static T Own<T>(T value, ICollection<IDisposable> owned) where T : IDisposable
    {
        owned.Add(value);
        return value;
    }
}
