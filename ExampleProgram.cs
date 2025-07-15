using System.Text;
using System.Threading.Channels;

namespace SPRDClientExample

{
    public class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("等待设备连接 (dl_diag)...");
                string port = SprdProtocolHandler.FindComPort(timeout: 30000);
                Console.WriteLine($"找到端口: {port}");
                SprdProtocolHandler handler = new(port, new HdlcEncoder());
                ComPortMonitor monitor = new(port);
                DeviceStatus status = new DeviceStatus();
                monitor.SetDisconnectedAction(() => { status.HasExited = true; monitor.Stop(); });
                SprdFlashUtils util = new(handler);
                ConsoleProgressBar bar = new();
                ProgressUpdater updater = new ProgressUpdater();
                updater.UpdateEvent += bar.UpdateProgress;
                util.UpdatePercentage += updater.UpdateProgress;
                util.UpdateStatus += bar.UpdateSpeed;
                util.Log += s => Console.WriteLine(s);
                var stages = util.ConnectToDevice();
                status.SprdMode = stages.SprdMode;
                status.NowStage = stages.Stage;
                Console.WriteLine($"SPRD版本:{status.SprdMode}");
                Console.WriteLine($"当前阶段:{status.NowStage}");
                CommandExecutor ce = new(util, status);
                while (!status.HasExited)
                {
                    Console.Write($"{status.NowStage} :");
                    string? a = Console.ReadLine();
                    await ce.ExecuteAsync(a == null ? "" : a);
                }
            }
            catch (TimeoutException)
            {
                Console.WriteLine("响应超时");
            }
        }
        class ConsoleProgressBar
        {
            public int BarWidth { get; set; } = 45;
            private object _lock = new();
            public void UpdateProgress(int percentage)
            {
                if (percentage > 100) percentage = 100;
                else if (percentage < 0) percentage = 0;
                int progressWidth = (int)(percentage / 100.0 * BarWidth);
                var tmp = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                lock (_lock)
                {
                    Console.CursorLeft = 0;
                    Console.Write('[');
                    Console.Write(new string('#', progressWidth));
                    Console.Write(new string(' ', BarWidth - progressWidth));
                    Console.Write(']');
                    Console.Write($"{percentage}%");
                    Console.ForegroundColor = tmp;
                    if (percentage == 100) Console.WriteLine();
                }
            }
            public void UpdateSpeed(string speed)
            {
                lock (_lock)
                {
                    var tmp = Console.CursorLeft;
                    Console.CursorLeft = BarWidth + 10;
                    Console.Write(speed);
                    Console.CursorLeft = tmp;
                }
            }
        }
        class ProgressUpdater : IDisposable
        {
            public event Action<int>? UpdateEvent;

            Task logTask;
            CancellationTokenSource cts = new();
            Channel<int> updateChannel = Channel.CreateBounded<int>
                (
                new BoundedChannelOptions(25)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropWrite

                });
            private async Task StartTask(CancellationToken ct)
            {
                await foreach (int i in updateChannel.Reader.ReadAllAsync(ct))
                {
                    UpdateEvent?.Invoke(i);
                }
            }

            public ProgressUpdater()
            {
                logTask = StartTask(cts.Token);
            }
            public void UpdateProgress(int percentage)
            {
                updateChannel.Writer.TryWrite(percentage);
            }
            public void Dispose()
            {
                cts.Cancel();
                updateChannel.Writer.Complete();
                try
                {
                    logTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { }
                GC.SuppressFinalize(this);
            }

        }
        public class DeviceStatus
        {
            public Stages NowStage { get; set; }
            public Stages SprdMode { get; set; }
            public bool HasExited { get; set; }
        }
        public class CommandExecutor(SprdFlashUtils utils, DeviceStatus status)
        {
            private readonly SprdFlashUtils utils = utils;
            private readonly DeviceStatus status = status;
            private CancellationTokenSource cts = new();

            private string commandHelp =
                @"指令帮助
[]内的参数必填, <>内的参数选填
发送并执行fdl：fdl [文件路径] [发送地址（0x格式）]
写入分区：w/write_part [分区名] [文件路径]
回读分区：r/read_part [分区名] <保存路径> <回读大小> <回读偏移量>
获取分区大小：ps/part_size [分区名]
检查分区是否存在：cp/check_part [分区名]
获取并保存当前分区表：pl/partition_list [保存路径] <获取方法，支持参数1,2,3>
关机: off/poweroff/exit
开机: rst/reset/poweron/po";

            private static List<string> ParseCommand(string command)
            {
                var result = new List<string>();
                var sb = new StringBuilder();
                bool inPath = false;

                foreach (char c in command)
                {
                    if (c == '"') inPath = !inPath;
                    else if (c == ' ' && !inPath)
                    {
                        if (sb.Length > 0)
                        {
                            result.Add(sb.ToString());
                            sb.Clear();
                        }
                    }
                    else sb.Append(c);
                }

                if (sb.Length > 0) result.Add(sb.ToString());

                return result;
            }
            public async Task ExecuteAsync(string command)
            {
                var args = ParseCommand(command);
                if (args.Count > 0)
                    switch (args[0])
                    {
                        default: Console.WriteLine(commandHelp); break;
                        case "fdl":
                            if (args.Count < 3 || !File.Exists(args[1]))
                            {
                                Console.WriteLine(commandHelp);
                                return;
                            }
                            if (status.NowStage >= Stages.Fdl2) return;
                            using (FileStream fs = File.OpenRead(args[1]))
                                utils.SendFile(fs, (uint)SprdFlashUtils.StringToSize(args[2]));
                            utils.ExecuteDataAndConnect(status.NowStage);
                            status.NowStage++;
                            break;
                        case "r" or "read_part":
                            if (status.NowStage != Stages.Fdl2) return;
                            if (args.Count < 2)
                            {
                                Console.WriteLine(commandHelp);
                                return;
                            }
                            if (utils.CheckPartitionExist(args[1]))
                                using (FileStream fs = File.Create(args.Count >= 3 && File.Exists(args[2]) ? args[2] : args[1] + ".img"))
                                    await utils.ReadPartitionCustomizeAsync(fs, args[1],
                                       args.Count >= 4 ?
                                       SprdFlashUtils.StringToSize(args[3])
                                       : utils.GetPartitionSize(args[1]),
                                        cts.Token
                                        , offset: args.Count >= 5 ? SprdFlashUtils.StringToSize(args[4]) : 0);
                            break;
                        case "w" or "write_part":
                            if (status.NowStage != Stages.Fdl2) return;
                            if (args.Count < 3)
                            {
                                return;
                            }
                            if (File.Exists(args[2]))
                                using (FileStream fs = File.OpenRead(args[2]))
                                    await utils.WritePartitionAsync(args[1], fs, cts.Token);
                            Console.WriteLine();
                            break;
                        case "ps" or "part_size":
                            if (status.NowStage != Stages.Fdl2) return;
                            if (args.Count < 2)
                            {
                                Console.WriteLine(commandHelp);
                                return;
                            }
                            if (utils.CheckPartitionExist(args[1]))
                                Console.WriteLine($"{utils.GetPartitionSize(args[1]) / 1024 / 1024}MB");
                            break;
                        case "cp" or "check_part":
                            if (args.Count < 2)
                            {
                                Console.WriteLine(commandHelp);
                                return;
                            }
                            Console.WriteLine(utils.CheckPartitionExist(args[1]) ? "存在" : "不存在");
                            break;
                        case "off" or "poweroff" or "exit":
                            utils.ShutdownDevice();
                            status.HasExited = true;
                            break;
                        case "rst" or "reset" or "poweron" or "po":
                            utils.PowerOnDevice();
                            status.HasExited = true;
                            break;
                    }
            }

            public void CancelAction()
            {
                cts.Cancel();
                cts = new();
            }
        }
    }
}