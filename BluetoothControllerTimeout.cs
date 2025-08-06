using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Gamepad;
using System.Diagnostics;
using System.Text.RegularExpressions;

class BluetoothControllerTimeout
{
    static readonly TimeSpan TimeoutLength = TimeSpan.FromMinutes(5);
    static readonly TimeSpan InputUpdateInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(2);

    static Dictionary<string, ControlerTimeout> Joysticks = [];

    static async Task Main(string[] args)
    {
        Console.Out.Write(string.Join(' ', args));
        Console.Out.Flush();


        while (true)
        {
            if (await BluetoothEnabled())
            {
                var _Joysticks = Joysticks;
                //Console.WriteLine($"Joysticks.Count = " + _Joysticks.Count);

                var newJoys = await GetBluetoothJoySticks();
                foreach (var pair in newJoys)
                {
                    if (!Joysticks.ContainsKey(pair.Key))
                    {
                        var controllerTimeout = new ControlerTimeout(pair.Key, pair.Value.controller, pair.Value.device);
                        Joysticks.Add(pair.Key, controllerTimeout);
                        Joysticks[pair.Key].TimeoutReached += (s, e) =>
                        {
                            Console.WriteLine($"Controller '{e.Uuiq}' reached timeout.");
                            Joysticks.Remove(e.Uuiq);
                        };
                        Joysticks[pair.Key].Disposed += (s, e) =>
                        {
                            if (Joysticks.Remove(e.Uuiq))
                                Console.WriteLine($"Lost controller '{e.Uuiq}'.");
                        };

                        Console.WriteLine($"Found controller '{pair.Key}'.");
                    }
                }
            }

            await Task.Delay(SearchInterval);
        }
    }

    public class ControlerTimeout : IDisposable
    {
        public readonly string Uuiq;
        public readonly GamepadController Controller;
        public readonly Linux.Bluetooth.Device Device;
        public bool IsDisposed { get; private set; }

        public EventHandler<ControlerTimeout>? TimeoutReached;
        public EventHandler<ControlerTimeout>? Disposed;

        readonly System.Timers.Timer _Timer;

        public ControlerTimeout(string uuiq, GamepadController controller, Device device)
        {
            Uuiq = uuiq;
            Controller = controller;
            Device = device;

            IsDisposed = false;
            Device.Disconnected += (s, e) => { Dispose(); return Task.CompletedTask; };

            _Timer = new(TimeoutLength) { AutoReset = false };
            _Timer.Elapsed += OnTimeoutReached;

            _ = ControllerInputUpdateLoop();
        }


        async Task ControllerInputUpdateLoop()
        {
            while (!IsDisposed)
            {
                int[] lastaxies = new int[256];
                bool[] lastbuttons = new bool[256];

                while (true)
                {
                    bool changeDetected = false;

                    foreach (var axis in Controller.Axis)
                    {
                        if (JoyStickHasChanged(lastaxies[axis.Key], axis.Value))
                        {
                            changeDetected = true;
                            lastaxies[axis.Key] = axis.Value;
                            //Console.WriteLine($"'{Uuiq}', axischanged:{axis.Key}");
                        }
                    }
                    foreach (var button in Controller.Buttons)
                    {
                        if (lastbuttons[button.Key] != button.Value)
                        {
                            changeDetected = true;
                            lastbuttons[button.Key] = button.Value;
                            //Console.WriteLine($"'{Uuiq}', buttonchanged:{button.Key}");
                        }
                    }

                    if (changeDetected)
                    {
                        _Timer.Stop();
                        _Timer.Start();
#if DEBUG
                        Console.WriteLine($"'{Uuiq}' Timer reset");
#endif
                    }

                    await Task.Delay(InputUpdateInterval);
                }
            }
        }

        void OnTimeoutReached(object? sender, System.Timers.ElapsedEventArgs? eventArgs)
        {
            TimeoutReached?.Invoke(this, this);
            Device.DisconnectAsync();//calls Dispose as well.
        }

        public void Dispose()
        {
            IsDisposed = true;
            Controller.Dispose();
            Device.Dispose();
            _Timer.Dispose();
            Disposed?.Invoke(this, this);
        }
        const int JoyRange = 65_536;
        const int JoyChangeThreshold = (int)(JoyRange * 0.25);
        static bool JoyStickHasChanged(int lastpos, int nextpos)
        {
            int lastup = lastpos + JoyChangeThreshold;
            int lastdown = lastpos - JoyChangeThreshold;

            if (lastdown < nextpos && nextpos < lastup)
                return false;
            else
                return true;
        }
    }




    static async Task<Dictionary<string, (GamepadController controller, Linux.Bluetooth.Device device)>> GetBluetoothJoySticks()
    {
        List<FileInfo> joyfiles = new DirectoryInfo("/dev/input/")
            .GetFiles()
            .Where(f => f.Name.StartsWith("js"))
            .ToList();

        Dictionary<string, GamepadController> joysticks = [];

        foreach (var js in joyfiles)
        {
            try
            {
                Process udevadm = new()
                {
                    StartInfo = new()
                    {
                        FileName = "udevadm",
                        Arguments = $"info -a {js.FullName}",
                        RedirectStandardOutput = true,
                    }
                };
                udevadm.Start();
                await udevadm.WaitForExitAsync();
                string output = udevadm.StandardOutput.ReadToEnd();

                var regex = Regex.Match(output, @"ATTRS\{uniq\}==""([0-9a-fA-F:]{17})""");
                if (regex.Success && !output.Contains("Motion Sensors"))//get better solution for motionsensors hack.
                {
                    if (!joysticks.TryAdd(regex.Groups[1].Value, new GamepadController(js.FullName)))
                        Console.WriteLine($"Found duplicate of device '{regex.Groups[1].Value}' {js.FullName}");
                }
            }
            catch { continue; }
        }

        Dictionary<string, (GamepadController controller, Linux.Bluetooth.Device device)> results = [];
        foreach (var pair in joysticks)
        {
            var dev = await FindDeviceFromUuiq(pair.Key);
            if (dev is null)
            {
                Console.WriteLine($"Can not find joystick '{pair.Key}'s bluetooth device.");
                continue;
            }

            if (await dev.GetConnectedAsync())
                results.Add(pair.Key, new(pair.Value, dev));
        }


        return results;
    }


    static async Task<Device?> FindDeviceFromUuiq(string uuiq)
    {
        try
        {
            var adapters = await BlueZManager.GetAdaptersAsync();

            foreach (var adapter in adapters)
            {
                var devices = await adapter.GetDevicesAsync();
                foreach (var device in devices)
                {
                    string addr = await device.GetAddressAsync();
                    if (string.Compare(addr, uuiq, true) == 0)
                    {
                        return device;
                    }
                };
            };
            return null;
        }
        catch
        {
            return null;
        }
    }

    static async Task<bool> BluetoothEnabled()
    {
        try { await BlueZManager.GetAdaptersAsync(); }
        catch { return false; }
        return true;
    }
}
