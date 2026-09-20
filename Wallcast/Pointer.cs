using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Wallcast;

// Sends a tap back to whatever the wallpaper is showing. The PC advertises itself as a Bluetooth
// mouse - a HID-over-GATT peripheral - and the device on screen pairs with it like any other
// pointing device, so nothing has to be installed at the far end.
//
// The reports carry an absolute position rather than a delta, which is what makes this worth doing:
// "the pixel under the cursor" arrives in one report, with no cursor to chase and no drift to
// correct. Measured on an iPad Pro: 0..32767 maps onto the whole screen with no scale error, and a
// tap aimed at a button by screen fraction landed on it.
//
// What it cannot do is touch. iPadOS draws a pointer for this and treats a click as a tap, so drags
// and scrolling work, but there is no second finger and so no pinch.
internal sealed class Pointer : IDisposable
{
    private const byte ReportId = 2;
    private static Guid Uuid(ushort id) => new($"0000{id:x4}-0000-1000-8000-00805f9b34fb");

    private GattServiceProvider? provider;
    private GattLocalCharacteristic? input;
    private readonly SemaphoreSlim pending = new(0, 1);
    private CancellationTokenSource? life;
    private int wantX, wantY, wantButtons, wantWheel;
    private int sentX = -1, sentY = -1, sentButtons;

    public event Action<string>? Status;

    // Set WALLCAST_TRACE to a path and every report written to the radio lands in it. A click that
    // does not take at the far end is otherwise indistinguishable from a click that was never sent.
    private static readonly string? Trace = Environment.GetEnvironmentVariable("WALLCAST_TRACE");
    /// <summary>Whether anything is listening, so a caller can skip building a line nobody reads.</summary>
    public static bool Tracing => Trace is not null;
    public static void Watch(string line) => Note(line);

    private static void Note(string line)
    {
        if (Trace is null) return;
        try { File.AppendAllText(Trace, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    /// <summary>Only this device is sent to. Every iOS device that has ever bonded with this PC
    /// answers a HID advertisement, and the first one to answer would otherwise get the taps.</summary>
    public string? Target { get; set; }

    public bool Advertising => provider?.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started;
    public bool Connected => Subscriber is not null;

    /// <summary>Whether this radio can be a peripheral at all. Plenty of adapters are central-only,
    /// and there is nothing to be done about it in software.</summary>
    public static async Task<bool> SupportedAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            return adapter is { IsLowEnergySupported: true, IsPeripheralRoleSupported: true };
        }
        catch (Exception problem) when (problem is not OutOfMemoryException) { return false; }
    }

    /// <summary>The devices already paired with this PC, to choose the one on screen from. Pairing
    /// itself happens in Windows' own Bluetooth settings, not here.</summary>
    public static async Task<List<(string Name, string Address)>> PairedAsync()
    {
        var found = new List<(string, string)>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
            foreach (var device in devices)
            {
                var address = Tail(device.Id);
                if (address.Length == 12 && !found.Any(seen => seen.Item2 == address))
                    found.Add((string.IsNullOrWhiteSpace(device.Name) ? address : device.Name, address));
            }
        }
        catch (Exception problem) when (problem is not OutOfMemoryException) { }
        return found;
    }

    public async Task StartAsync()
    {
        if (provider is not null) return;
        var adapter = await BluetoothAdapter.GetDefaultAsync()
            ?? throw new InvalidOperationException("No Bluetooth adapter.");
        if (!adapter.IsPeripheralRoleSupported)
            throw new InvalidOperationException("This Bluetooth radio cannot act as a mouse for another device.");

        var service = await GattServiceProvider.CreateAsync(Uuid(0x1812));
        if (service.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Windows refused the HID service: {service.Error}");
        provider = service.ServiceProvider;

        // bcdHID 1.11, no country code, RemoteWake | NormallyConnectable.
        await ReadableAsync(Uuid(0x2A4A), [0x11, 0x01, 0x00, 0x03]);
        await ReadableAsync(Uuid(0x2A4B), ReportMap);
        await ControlAsync(Uuid(0x2A4C), GattCharacteristicProperties.WriteWithoutResponse);
        await ControlAsync(Uuid(0x2A4E), GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse);
        input = await InputAsync();

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AdvertisementStatusChanged += (_, args) =>
        {
            if (args.Status == GattServiceProviderAdvertisementStatus.Started) started.TrySetResult(true);
        };
        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });

        // The provider reads back Aborted until the radio actually begins, so the status right here
        // says nothing; only the transition does.
        if (await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(10))) != started.Task)
            Status?.Invoke("Bluetooth did not start advertising; the device cannot find this PC.");

        life = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(life.Token));
    }

    /// <summary>Where the pointer should be, as a fraction of the picture. Called from a mouse hook,
    /// so it only leaves a note for the pump: a hook that waits on a radio stops the mouse.</summary>
    public void MoveTo(double across, double down)
    {
        Interlocked.Exchange(ref wantX, (int)Math.Round(Math.Clamp(across, 0, 1) * 32767));
        Interlocked.Exchange(ref wantY, (int)Math.Round(Math.Clamp(down, 0, 1) * 32767));
        Wake();
    }

    public void Button(int which, bool down)
    {
        Note($"button {which} {(down ? "down" : "up")}");
        var bit = which switch { 2 => 0x02, 3 => 0x04, _ => 0x01 };
        var held = Volatile.Read(ref wantButtons);
        Interlocked.Exchange(ref wantButtons, down ? held | bit : held & ~bit);
        Wake();
    }

    public void Wheel(int clicks)
    {
        Interlocked.Add(ref wantWheel, clicks);
        Wake();
    }

    /// <summary>Which buttons the far end has been told are down.</summary>
    public int Held => Volatile.Read(ref wantButtons);

    /// <summary>Send anything the far end has not been told yet. A write that failed is not retried
    /// on its own - the pump only stirs when something changes - so a release lost to a bad moment
    /// on the radio would stay lost. This costs nothing when there is nothing to say.</summary>
    public void Flush() => Wake();

    public void Release()
    {
        Interlocked.Exchange(ref wantButtons, 0);
        Wake();
    }

    private void Wake() { try { pending.Release(); } catch (SemaphoreFullException) { } }

    // One writer, fed by a slot rather than a queue. A mouse produces far more moves than a radio
    // will carry, and only the newest position means anything, so the ones in between are dropped
    // rather than queued into a lag. A wheel click is the exception: those add up.
    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await pending.WaitAsync(token); } catch (OperationCanceledException) { return; }
            var client = Subscriber;
            if (client is null) continue;

            var x = Volatile.Read(ref wantX);
            var y = Volatile.Read(ref wantY);
            var buttons = Volatile.Read(ref wantButtons);
            var wheel = Interlocked.Exchange(ref wantWheel, 0);
            if (x == sentX && y == sentY && buttons == sentButtons && wheel == 0) continue;
            var wasX = sentX; var wasY = sentY; var wasButtons = sentButtons;

            try
            {
                // A real mouse never moves and acts in the same instant, and the far end counts on
                // that: iPadOS glides its pointer onto whatever is under it and only then can the
                // thing be pressed or scrolled. A single report that teleports and presses at once
                // draws the press animation and does nothing at all - measured, and the reason a
                // click that was plainly sent did not take. A wheel is the same: it scrolls whatever
                // the pointer had settled on, so one that arrives with a jump has nothing to scroll.
                // The position goes first, on its own, and the rest follows a beat later.
                if ((buttons != wasButtons || wheel != 0) && (x != wasX || y != wasY))
                {
                    Note($"settle at {x},{y}");
                    await NotifyAsync(client, Payload((byte)wasButtons, (ushort)x, (ushort)y, 0));
                    await Task.Delay(40, token);
                }
                Note($"send buttons={buttons} at {x},{y} wheel={wheel}");
                await NotifyAsync(client, Payload((byte)buttons, (ushort)x, (ushort)y, wheel));
                // Recorded only now. Claiming it before the write means a failed write is never
                // retried, because the next round sees no change and skips - and if what failed was
                // a release, the far end is left pressed for good.
                sentX = x; sentY = y; sentButtons = buttons;
            }
            catch (Exception problem) when (problem is not OutOfMemoryException)
            { Status?.Invoke("Bluetooth: " + problem.Message); }

            // A connection interval is around 15ms and there is no point writing faster than the
            // link will carry; this also stops a frantic mouse from starving everything else.
            await Task.Delay(12, token).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private GattSubscribedClient? Subscriber => input?.SubscribedClients
        .FirstOrDefault(client => Target is null || Tail(client.Session.DeviceId.Id).Equals(Target, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tail of a device id is its Bluetooth address, which is the only thing that tells
    /// the tablet on the wall from the phone in a pocket.</summary>
    private static string Tail(string id) =>
        (id.Split('-').LastOrDefault() ?? "").Replace(":", "").Replace("#", "");

    private static byte[] Payload(byte buttons, ushort x, ushort y, int wheel) =>
    [
        buttons,
        (byte)(x & 0xFF), (byte)(x >> 8),
        (byte)(y & 0xFF), (byte)(y >> 8),
        unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127)),
    ];

    private async Task NotifyAsync(GattSubscribedClient client, byte[] report)
    {
        var writer = new DataWriter();
        writer.WriteBytes(report);
        await input!.NotifyValueAsync(writer.DetachBuffer(), client);
    }

    private async Task ReadableAsync(Guid uuid, byte[] value) =>
        await Create(uuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = CryptographicBuffer.CreateFromByteArray(value),
        });

    private async Task ControlAsync(Guid uuid, GattCharacteristicProperties properties) =>
        await Create(uuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = CryptographicBuffer.CreateFromByteArray([0x01]),   // report protocol
        });

    private async Task<GattLocalCharacteristic> Create(Guid uuid, GattLocalCharacteristicParameters parameters)
    {
        var result = await provider!.Service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Bluetooth characteristic {uuid}: {result.Error}");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> InputAsync()
    {
        var characteristic = await Create(Uuid(0x2A4D), new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = CryptographicBuffer.CreateFromByteArray(new byte[6]),
        });
        // Which report this carries and in which direction. Without it the host has nothing to match
        // the notification against, and the report ID is deliberately not in the payload.
        var reference = await characteristic.CreateDescriptorAsync(Uuid(0x2908), new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            StaticValue = CryptographicBuffer.CreateFromByteArray([ReportId, 1]),
        });
        if (reference.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Bluetooth report reference: {reference.Error}");

        characteristic.SubscribedClientsChanged += (sender, _) =>
        {
            // A fresh connection knows nothing of where the pointer was, so make the next report say
            // it outright rather than being skipped as unchanged. The buttons matter more than the
            // position: a link that dropped mid-click leaves the far end holding one, and it will
            // ignore everything until it is told otherwise. Say "nothing is pressed" outright.
            sentX = sentY = -1;
            sentButtons = -1;
            Interlocked.Exchange(ref wantButtons, 0);
            Wake();
            Status?.Invoke(sender.SubscribedClients.Any(client => Target is null
                || Tail(client.Session.DeviceId.Id).Equals(Target, StringComparison.OrdinalIgnoreCase))
                ? "Clicks are reaching the device."
                : "Waiting for the device to connect to this PC.");
        };
        return characteristic;
    }

    // A mouse on report ID 2. Ordinary in every respect but the two axes, which carry a position
    // rather than a movement.
    private static readonly byte[] ReportMap =
    [
        0x05, 0x01,             // Usage Page (Generic Desktop)
        0x09, 0x02,             // Usage (Mouse)
        0xA1, 0x01,             // Collection (Application)
        0x85, ReportId,         //   Report ID (2)
        0x09, 0x01,             //   Usage (Pointer)
        0xA1, 0x00,             //   Collection (Physical)
        0x05, 0x09,             //     Usage Page (Button)
        0x19, 0x01, 0x29, 0x03, //     Usage 1..3
        0x15, 0x00, 0x25, 0x01, //     Logical 0..1
        0x95, 0x03, 0x75, 0x01, //     3 x 1 bit
        0x81, 0x02,             //     Input (Data,Var,Abs) - buttons
        0x95, 0x01, 0x75, 0x05, //     1 x 5 bits
        0x81, 0x01,             //     Input (Const) - padding to a byte
        0x05, 0x01,             //     Usage Page (Generic Desktop)
        0x09, 0x30, 0x09, 0x31, //     Usage (X), Usage (Y)
        0x16, 0x00, 0x00,       //     Logical Minimum (0)
        0x26, 0xFF, 0x7F,       //     Logical Maximum (32767)
        0x75, 0x10, 0x95, 0x02, //     2 x 16 bits
        0x81, 0x02,             //     Input (Data,Var,Abs) - the position itself
        0x09, 0x38,             //     Usage (Wheel)
        0x15, 0x81, 0x25, 0x7F, //     -127..127
        0x75, 0x08, 0x95, 0x01, //     1 x 8 bits
        0x81, 0x06,             //     Input (Data,Var,Rel) - a wheel has no position
        0xC0,                   //   End Collection
        0xC0,                   // End Collection
    ];

    public void Dispose()
    {
        // Leaving a button held on another device would be rude, and it is the one piece of state
        // that outlives this process.
        if (Connected && Volatile.Read(ref wantButtons) != 0)
        {
            try { NotifyAsync(Subscriber!, Payload(0, (ushort)sentX, (ushort)sentY, 0)).Wait(200); }
            catch (Exception problem) when (problem is not OutOfMemoryException) { }
        }
        life?.Cancel();
        life?.Dispose();
        life = null;
        if (Advertising) provider!.StopAdvertising();
        provider = null;
        input = null;
    }
}
