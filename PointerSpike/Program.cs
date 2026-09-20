using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace PointerSpike;

// Does an iPad take an absolute pointer from this PC over BLE, and does the button come back up?
//
// That question decides whether Wallcast can send a click back to what it is showing, and nothing
// in the app should be written until it is answered. This is the whole chain in one console app:
// the PC advertises itself as a HID-over-GATT peripheral, the iPad pairs with it as an ordinary
// Bluetooth mouse, and every report is typed in by hand so a failure can be pinned on one report
// rather than on a pipeline.
//
// The Windows half is already known to work - the shape of the service, the protection levels and
// the advertising dance are all as in abhishek-raj/windows-ble-hid (MIT), which drives Android and
// iOS with a relative mouse. The single deliberate difference is the pointer axes: absolute, 0 to
// 32767, rather than relative deltas. If that difference is the thing iPadOS rejects, it shows up
// here in two minutes.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var relative = args.Contains("--relative");   // the known-good descriptor, for comparison
        var plain = args.Contains("--plain");         // HOGP wants encryption; some hosts bond oddly
        var sweep = args.Contains("--sweep");
        // Typing into a console is one more thing to hold while watching a screen across the room,
        // so a whole run can be written out in advance and the only job left is to look at the iPad.
        var script = Value(args, "--do");
        // Every iOS device that has ever bonded with this PC races to answer a HID advertisement,
        // and the nearest phone wins. Naming the intended one keeps the reports off the others.
        var only = Value(args, "--only");

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(relative
            ? "pointer: RELATIVE (control run - deltas, like any mouse)"
            : "pointer: ABSOLUTE 0..32767 (the thing being tested)");

        var pointer = new Peripheral(relative, !plain) { Only = only };
        pointer.Log += line => Console.WriteLine(line);

        try { await pointer.StartAsync(); }
        catch (Exception problem) { Console.WriteLine($"stopped: {problem.Message}"); return 1; }

        Console.WriteLine();
        Console.WriteLine("On the iPad: Settings > Bluetooth, pair with this PC, then");
        Console.WriteLine("Settings > Accessibility > Touch > AssistiveTouch = on, or no pointer is drawn.");
        Console.WriteLine();
        Help();

        if (script is not null)
        {
            if (!await pointer.WaitForClientAsync(TimeSpan.FromSeconds(180)))
            {
                Console.WriteLine("nothing connected - pair the iPad with this PC while this is running");
                pointer.Dispose();
                return 2;
            }
            foreach (var step in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var words = step.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Console.WriteLine($"> {step.Trim()}");
                if (words[0] == "wait") { await Task.Delay(int.Parse(words[1])); continue; }
                if (words[0] == "hold") { await pointer.HoldAsync(int.Parse(words[1])); continue; }
                if (words[0] == "note") { Console.WriteLine($"  -- {string.Join(' ', words[1..])}"); continue; }
                try { await Run(pointer, words); }
                catch (Exception problem) { Console.WriteLine($"  ! {problem.Message}"); }
            }
            pointer.Dispose();
            return 0;
        }

        if (sweep) { await Sweep(pointer); return 0; }

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null or "q" or "quit") break;
            try { await Run(pointer, line.Split(' ', StringSplitOptions.RemoveEmptyEntries)); }
            catch (Exception problem) { Console.WriteLine($"  ! {problem.Message}"); }
        }

        pointer.Dispose();
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void Help()
    {
        Console.WriteLine("""
            m <x%> <y%>   move the pointer, 0-100 across the iPad's screen
            c [x%] [y%]   click, optionally moving there first
            d | u         press and release on their own, to see which half fails
            w <n>         wheel, -127..127
            raw <x> <y>   move in HID units, 0..32767, to check the ends of the range
            id            toggle the report-ID byte in front of the payload (an iOS workaround)
            s             status: advertising, subscribers
            q             quit
            """);
    }

    private static async Task Run(Peripheral pointer, string[] words)
    {
        switch (words.FirstOrDefault())
        {
            case null: return;
            case "?" or "h" or "help": Help(); return;
            case "s": pointer.Report(); return;
            case "id":
                pointer.PrefixReportId = !pointer.PrefixReportId;
                Console.WriteLine($"  report-ID prefix: {(pointer.PrefixReportId ? "on (7 bytes)" : "off (6 bytes, per spec)")}");
                return;

            case "m" when words.Length == 3:
                await pointer.MoveAsync(Percent(words[1]), Percent(words[2]));
                return;

            case "raw" when words.Length == 3:
                await pointer.MoveRawAsync(ushort.Parse(words[1]), ushort.Parse(words[2]));
                return;

            case "c":
                if (words.Length == 3) await pointer.MoveAsync(Percent(words[1]), Percent(words[2]));
                // A tap is two reports. Hold them apart so a host that coalesces them is visible as
                // a press with no release rather than as nothing happening at all.
                await pointer.ButtonAsync(true);
                await Task.Delay(60);
                await pointer.ButtonAsync(false);
                Console.WriteLine("  down, 60ms, up");
                return;

            case "d": await pointer.ButtonAsync(true); Console.WriteLine("  down"); return;
            case "u": await pointer.ButtonAsync(false); Console.WriteLine("  up"); return;
            case "w" when words.Length == 2: await pointer.WheelAsync(int.Parse(words[1])); return;

            default: Console.WriteLine("  ?"); return;
        }

        static double Percent(string value) => Math.Clamp(double.Parse(value), 0, 100) / 100.0;
    }

    // Corners first: they are where an absolute range that is off by a scale factor gives itself
    // away, because the pointer stops short of the edge instead of reaching it.
    private static async Task Sweep(Peripheral pointer)
    {
        foreach (var (x, y, name) in new[]
                 {
                     (0.5, 0.5, "centre"), (0.02, 0.02, "top left"), (0.98, 0.02, "top right"),
                     (0.98, 0.98, "bottom right"), (0.02, 0.98, "bottom left"), (0.5, 0.5, "centre"),
                 })
        {
            Console.WriteLine($"  -> {name}");
            await pointer.MoveAsync(x, y);
            await Task.Delay(1200);
        }
    }
}

internal sealed class Peripheral(bool relative, bool encrypted) : IDisposable
{
    private const byte KeyboardReportId = 1, PointerReportId = 2;
    private static Guid Uuid(ushort id) => new($"0000{id:x4}-0000-1000-8000-00805f9b34fb");

    private GattServiceProvider? provider;
    private GattLocalCharacteristic? input;
    private TaskCompletionSource<bool>? advertising;
    private ushort x, y;
    private byte buttons;

    private GattProtectionLevel Protection =>
        encrypted ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain;

    public event Action<string>? Log;

    /// <summary>Off by spec - the Report Reference descriptor carries the ID - but iOS hosts have
    /// been reported to need it in the payload, so it is a switch rather than a decision.</summary>
    public bool PrefixReportId { get; set; }

    /// <summary>Send only to the client whose address contains this, when more than one answers.</summary>
    public string? Only { get; init; }

    /// <summary>The tail of a subscriber's device id is its Bluetooth address, which is the only
    /// thing that tells one silently-connected phone from the tablet that was meant.</summary>
    private static string Address(GattSubscribedClient client) =>
        client.Session.DeviceId.Id.Split('-').LastOrDefault()?.Replace(":", "") ?? "";

    public async Task StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync() ?? throw new InvalidOperationException("No Bluetooth adapter.");
        Say($"adapter: LE={adapter.IsLowEnergySupported} peripheral={adapter.IsPeripheralRoleSupported}");
        if (!adapter.IsPeripheralRoleSupported)
            throw new NotSupportedException("This radio will not take the LE peripheral role.");

        var service = await GattServiceProvider.CreateAsync(Uuid(0x1812));
        if (service.Error != BluetoothError.Success)
            throw new InvalidOperationException($"HID service 0x1812: {service.Error}");
        provider = service.ServiceProvider;
        Say("HID service 0x1812: created");

        // bcdHID 1.11, no country code, RemoteWake | NormallyConnectable.
        await ReadableAsync("HID Information 0x2A4A", Uuid(0x2A4A), [0x11, 0x01, 0x00, 0x03]);
        await ReadableAsync("Report Map 0x2A4B", Uuid(0x2A4B), Descriptor(relative));
        await ControlAsync("HID Control Point 0x2A4C", Uuid(0x2A4C), GattCharacteristicProperties.WriteWithoutResponse);
        await ControlAsync("Protocol Mode 0x2A4E", Uuid(0x2A4E),
            GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse);

        input = await InputAsync("pointer report", PointerReportId);

        advertising = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AdvertisementStatusChanged += (sender, args) =>
        {
            Say($"advertising: {args.Status}");
            if (args.Status == GattServiceProviderAdvertisementStatus.Started) advertising?.TrySetResult(true);
        };
        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });

        // The provider says Aborted until the radio actually begins, so the status right here means
        // nothing; the transition is the only honest signal.
        if (await Task.WhenAny(advertising.Task, Task.Delay(TimeSpan.FromSeconds(10))) != advertising.Task)
            Say("advertising did not start within 10s - the radio may be busy or blocked by policy");
    }

    public Task MoveAsync(double across, double down) =>
        MoveRawAsync((ushort)Math.Round(across * 32767), (ushort)Math.Round(down * 32767));

    public async Task MoveRawAsync(ushort across, ushort down)
    {
        // Relative mode cannot be told where to go, only how far to shift, so a move there is the
        // difference from where we last claimed to be. It will drift; that is the point of it being
        // the control run and not the design.
        var report = relative
            ? Payload(buttons, (short)(across - x), (short)(down - y), 0)
            : Payload(buttons, (short)across, (short)down, 0);
        x = across; y = down;
        await NotifyAsync(report);
    }

    public Task ButtonAsync(bool down)
    {
        buttons = (byte)(down ? 0x01 : 0x00);
        // The position is repeated with the button so the host cannot place the click somewhere
        // other than where the pointer was left.
        return NotifyAsync(relative ? Payload(buttons, 0, 0, 0) : Payload(buttons, (short)x, (short)y, 0));
    }

    public Task WheelAsync(int clicks) =>
        NotifyAsync(relative ? Payload(buttons, 0, 0, clicks) : Payload(buttons, (short)x, (short)y, clicks));

    /// <summary>A host has to connect and turn notifications on before a report goes anywhere, and
    /// that takes as long as it takes someone to walk to the iPad.</summary>
    public async Task<bool> WaitForClientAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        var announced = false;
        var next = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            var ready = input?.SubscribedClients.Any(client =>
                Only is null || Address(client).Contains(Only, StringComparison.OrdinalIgnoreCase)) == true;
            if (ready) { Say("connected"); return true; }
            if (!announced) { Say("waiting for something to connect and subscribe..."); announced = true; }
            // Some stacks quietly stop advertising once any central connects, which would leave the
            // intended device unable to see this PC at all. Say so rather than waiting in silence.
            if (DateTime.UtcNow >= next)
            {
                next = DateTime.UtcNow.AddSeconds(10);
                Say($"still waiting - advertising: {provider?.AdvertisementStatus}, "
                    + $"subscribers: [{string.Join(", ", input?.SubscribedClients.Select(Address) ?? [])}]");
            }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>iPadOS fades the pointer out after a moment of stillness, so a position that is
    /// merely held looks like a position that never arrived. A single unit of dither is under one
    /// screen pixel and keeps it awake without moving it anywhere.</summary>
    public async Task HoldAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var here = (x, y);
        var nudge = false;
        while (DateTime.UtcNow < until)
        {
            nudge = !nudge;
            await MoveRawAsync((ushort)Math.Min(32767, here.x + (nudge ? 1 : 0)),
                               (ushort)Math.Min(32767, here.y + (nudge ? 1 : 0)));
            await Task.Delay(200);
        }
        await MoveRawAsync(here.x, here.y);
    }

    public void Report()
    {
        Say($"advertising: {provider?.AdvertisementStatus.ToString() ?? "not started"}");
        Say($"subscribers: {input?.SubscribedClients.Count ?? 0}");
        Say($"pointer at: {x},{y}  buttons: {buttons}  report-ID prefix: {PrefixReportId}");
    }

    private byte[] Payload(byte held, short across, short down, int wheel)
    {
        byte[] body =
        [
            held,
            (byte)(across & 0xFF), (byte)((across >> 8) & 0xFF),
            (byte)(down & 0xFF), (byte)((down >> 8) & 0xFF),
            unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127)),
        ];
        return PrefixReportId ? [PointerReportId, .. body] : body;
    }

    private async Task NotifyAsync(byte[] report)
    {
        if (input is null) throw new InvalidOperationException("Not started.");
        if (input.SubscribedClients.Count == 0)
        {
            Say("nobody is subscribed - the iPad is not connected, or has not enabled notifications yet");
            return;
        }
        foreach (var client in input.SubscribedClients)
        {
            if (Only is not null && !Address(client).Contains(Only, StringComparison.OrdinalIgnoreCase)) continue;
            var writer = new DataWriter();
            writer.WriteBytes(report);
            var sent = await input.NotifyValueAsync(writer.DetachBuffer(), client);
            if (sent.Status != GattCommunicationStatus.Success) Say($"notify: {sent.Status}");
        }
    }

    private async Task ReadableAsync(string name, Guid uuid, byte[] value)
    {
        var result = await provider!.Service.CreateCharacteristicAsync(uuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = Protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray(value),
        });
        Say($"{name}: {result.Error}");
    }

    private async Task ControlAsync(string name, Guid uuid, GattCharacteristicProperties properties)
    {
        var result = await provider!.Service.CreateCharacteristicAsync(uuid, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            ReadProtectionLevel = Protection,
            WriteProtectionLevel = Protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray([0x01]),   // report protocol
        });
        Say($"{name}: {result.Error}");
    }

    private async Task<GattLocalCharacteristic> InputAsync(string name, byte reportId)
    {
        var result = await provider!.Service.CreateCharacteristicAsync(Uuid(0x2A4D), new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = Protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray(new byte[6]),
        });
        Say($"{name} 0x2A4D: {result.Error}");
        var characteristic = result.Characteristic;

        // Which report this characteristic carries, and in which direction. Without it a host has
        // no way to match the notification to the descriptor it read.
        var reference = await characteristic.CreateDescriptorAsync(Uuid(0x2908), new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = Protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, 1]),
        });
        Say($"{name} / Report Reference 0x2908: {reference.Error} (id={reportId}, input)");

        characteristic.SubscribedClientsChanged += (sender, _) =>
        {
            Say($"subscribers: {sender.SubscribedClients.Count}");
            foreach (var client in sender.SubscribedClients)
                Say($"  subscriber: {Address(client)}{(Only is null ? "" : Address(client).Contains(Only, StringComparison.OrdinalIgnoreCase) ? "  <- target" : "  (ignored)")}");
        };
        return characteristic;
    }

    // Mouse on report ID 2. Everything here is ordinary except the two axes, which is the one thing
    // being asked about: Absolute over a 0..32767 range instead of signed deltas.
    private static byte[] Descriptor(bool relative)
    {
        byte[] axes = relative
            ? [0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F]    // -32767 .. 32767
            : [0x16, 0x00, 0x00, 0x26, 0xFF, 0x7F];   //      0 .. 32767
        byte flag = relative ? (byte)0x06 : (byte)0x02;   // Input (Data,Var,Rel) or (Data,Var,Abs)
        return
        [
            0x05, 0x01,             // Usage Page (Generic Desktop)
            0x09, 0x02,             // Usage (Mouse)
            0xA1, 0x01,             // Collection (Application)
            0x85, PointerReportId,  //   Report ID (2)
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
            ..axes,
            0x75, 0x10, 0x95, 0x02, //     2 x 16 bits
            0x81, flag,             //     the axes themselves
            0x09, 0x38,             //     Usage (Wheel)
            0x15, 0x81, 0x25, 0x7F, //     -127..127
            0x75, 0x08, 0x95, 0x01, //     1 x 8 bits
            0x81, 0x06,             //     Input (Data,Var,Rel) - a wheel has no absolute position
            0xC0,                   //   End Collection
            0xC0,                   // End Collection
        ];
    }

    private void Say(string line) => Log?.Invoke($"  {line}");

    public void Dispose()
    {
        if (provider?.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
            provider.StopAdvertising();
    }
}
