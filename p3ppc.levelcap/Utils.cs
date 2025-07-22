using System.Diagnostics;
using System.Text;
using p3ppc.levelcap.Configuration;
using Reloaded.Memory.Sigscan;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace p3ppc.levelcap
{
    public class Utils
    {
        private static ILogger _logger;
        private static IStartupScanner _startupScanner;
        internal static nint BaseAddress { get; private set; }

        internal static void Log(string message)
        {
            _logger.WriteLine($"[Alternate Menu Themes] {message}");
        }

        internal static void LogError(string message, Exception e)
        {
            _logger.WriteLine($"[Alternate Menu Themes] {message}: {e.Message}", System.Drawing.Color.Red);
        }

        internal static void LogError(string message)
        {
            _logger.WriteLine($"[Alternate Menu Themes] {message}", System.Drawing.Color.Red);
        }

        internal static void SigScan(string pattern, string name, Action<nint> action)
        {
            _startupScanner.AddMainModuleScan(pattern, result =>
            {
                if (!result.Found)
                {
                    LogError($"Unable to find {name}, stuff won't work :(");
                    return;
                }
                Log($"Found {name} at 0x{result.Offset + BaseAddress:X}");
                action(result.Offset + BaseAddress);
            });
        }

        internal static bool Initialise(ILogger logger, Config config, IModLoader modLoader)
        {
            _logger = logger;
            config = config;
            using var thisProcess = Process.GetCurrentProcess();
            BaseAddress = thisProcess.MainModule!.BaseAddress;

            var startupScannerController = modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
            {
                LogError($"Unable to get controller for Reloaded SigScan Library, stuff won't work :(");
                return false;
            }

            return true;

        }

        public static string PushXmm(int xmmNum)
        {
            return $"sub rsp, 16\n" + // Allocate space on stack
                   $"movdqu dqword [rsp], xmm{xmmNum}\n";
        }

        public static string PushXmm()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                sb.Append(PushXmm(i));
            }
            return sb.ToString();
        }

        public static string PopXmm(int xmmNum)
        {
            return $"movdqu xmm{xmmNum}, dqword [rsp]\n" +
                   $"add rsp, 16\n"; // Re-align the stack
        }

        public static string PopXmm()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 7; i >= 0; i--)
            {
                sb.Append(PopXmm(i));
            }
            return sb.ToString();
        }

        internal static unsafe nuint GetGlobalAddress(nint ptrAddress)
        {
            return (nuint)((*(int*)ptrAddress) + ptrAddress + 4);
        }

        internal static void SigScan(string pattern, string name, int[] indexes, Action<nint> action)
        {
            using var thisProcess = Process.GetCurrentProcess();
            using var scanner = new Scanner(thisProcess, thisProcess.MainModule);
            int offset = 0;
            int maxIndex = indexes.Max();

            for (int i = 0; i < maxIndex + 1; i++)
            {
                var result = scanner.FindPattern(pattern, offset);

                if (!result.Found)
                {
                    LogError($"Unable to find {name} at index {i}, stuff won't work :(");
                    return;
                }

                if (indexes.Contains(i))
                {
                    Log($"Found {name} ({i}) at 0x{result.Offset + BaseAddress:X}");
                    action(result.Offset + BaseAddress);
                }
                offset = result.Offset + 1;
            }
        }

        internal static void SigScan(string pattern, string name, int index, Action<nint> action)
        {
            SigScan(pattern, name, new int[] { index }, action);
        }

        internal static void SigScanAll(string pattern, string name, Action<nint> action)
        {
            using var thisProcess = Process.GetCurrentProcess();
            using var scanner = new Scanner(thisProcess, thisProcess.MainModule);
            int offset = 0;
            var result = scanner.FindPattern(pattern, offset);
            int i = 0;
            while (result.Found)
            {
                Log($"Found {name} ({i++}) at 0x{result.Offset + BaseAddress:X}");
                action(result.Offset + BaseAddress);
                offset = result.Offset + 1;
                result = scanner.FindPattern(pattern, offset);
            }
        }
    }
}