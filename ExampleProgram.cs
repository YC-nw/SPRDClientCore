using System.Text;
using System.Threading.Channels;

namespace SPRDClientExample

{
    public class Program
    {
        private static object _lock = new();
        public static void Log(string log)
        {
            lock (_lock) Console.WriteLine(log);
        }
        public static void Log(string log, ConsoleColor color)
        {
            lock (_lock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(log);
                Console.ForegroundColor = ConsoleColor.White;
            }
        }
        static async Task Main(string[] args)
        {
            try
            {
                var cfg = ConnectionConfig.Parse(ref args);
                Log("等待设备连接 (dl_diag)...");
                string port = SprdProtocolHandler.FindComPort(timeout: cfg.WaitTime);
                Log($"找到端口: {port}");
                SprdProtocolHandler handler = new(port, new HdlcEncoder());
                if (cfg.Method != null)
                {
                    ChangeDiagnosticMode(handler, null, null, cfg.Method ?? MethodOfChangingDiagnostic.CommonMode, cfg.ModeToChange);
                }
                ComPortMonitor monitor = new(port);
                DeviceStatus status = new DeviceStatus();
                monitor.SetDisconnectedAction(() => { status.HasExited = true; monitor.Stop(); });
                SprdFlashUtils util = new(handler);
                util.Timeout = cfg.Timeout;
                ConsoleProgressBar bar = new();
                ProgressUpdater updater = new ProgressUpdater();
                updater.UpdateEvent += bar.UpdateProgress;
                util.UpdatePercentage += updater.UpdateProgress;
                util.UpdateStatus += bar.UpdateSpeed;
                util.Log += Log;
                var stages = util.ConnectToDevice();
                status.SprdMode = stages.SprdMode;
                status.NowStage = stages.Stage;
                Log($"SPRD版本:{status.SprdMode}");
                Log($"当前阶段:{status.NowStage}");
                if (status.SprdMode == Stages.Sprd4)
                {
                    for (; status.NowStage < Stages.Fdl2; status.NowStage++)
                    {
                        util.ExecuteDataAndConnect(status.NowStage);
                    }
                }
                CommandExecutor ce = new(util, status);
                Log("按下Ctrl + C键以取消读写分区操作", ConsoleColor.Blue);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    ce.CancelAction();
                };
                bool hasGottenPartitionList = false;
                await ce.ExecuteAsync(args.ToList());
                while (!status.HasExited)
                {
                    try
                    {
                        if (status.NowStage == Stages.Fdl2 && !hasGottenPartitionList)
                        {
                            var p = util.GetPartitionsAndStorageInfo();
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            foreach (Partition partition in p.partitions) Log(partition.ToString());
                            Console.ForegroundColor = ConsoleColor.White;
                            using (FileStream fs = File.Create("partition_list.xml"))
                                SavePartitionsToXml(p.partitions, fs);
                            Log("已保存分区表");
                            hasGottenPartitionList = true;
                            status.PartitionList = p.partitions;
                            status.Method = p.finalMethod;
                        }
                        Console.Write($"{status.NowStage} :");
                        string? a = Console.ReadLine();
                        await ce.ExecuteAsync(a ?? "");
                    }
                    catch (Exception e)
                    {
                        Log($"发生错误: {e.Message}");
                    }
                }
            }
            catch (TimeoutException)
            {
                Log("响应超时");
            }
            catch (Exception e)
            {
                Log($"发生错误: {e}");
            }
            finally
            {
                Log("the program has exited. press any key to close this window");
                Console.ReadKey();
            }
        }
        class ConsoleProgressBar
        {
            public int BarWidth { get; set; } = 45;
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
            public List<Partition>? PartitionList { get; set; }
            public GetPartitionsMethod Method { get; set; }
            public string? ExecAddrFilePath { get; set; } = null;
            public uint ExecAddrSendAddress { get; set; }
            public bool IsAbleToSendExecAddr => NowStage == Stages.Brom && ExecAddrFilePath != null && ExecAddrSendAddress != 0;
        }
        public class ConnectionConfig
        {
            public uint WaitTime { get; set; } = 30000;
            public int Timeout { get; set; } = 5000;
            public MethodOfChangingDiagnostic? Method { get; set; } = null;
            public ModeToChange ModeToChange { get; set; }
            public byte VerboseLevel { get; set; } = 0;
            public static ConnectionConfig Parse(ref string[] args)
            {
                ConnectionConfig config = new();
                List<string> cmds = new();

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i].ToLowerInvariant();
                    bool matched = true;

                    switch (arg)
                    {
                        case "--wait":
                            if (i + 1 < args.Length && uint.TryParse(args[i + 1], out uint waitTime))
                            {
                                config.WaitTime = waitTime * 1000;
                                i++;
                            }
                            break;
                        case "--timeout":
                            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int timeout))
                            {
                                config.Timeout = timeout;
                                i++;
                            }
                            break;
                        case "--kick":
                            config.Method = MethodOfChangingDiagnostic.CommonMode;
                            config.ModeToChange = ModeToChange.DlDiagnostic;
                            break;
                        case "--kickto":
                            if (i + 1 < args.Length && byte.TryParse(args[i + 1], out var mode))
                            {
                                config.ModeToChange = (ModeToChange)mode;
                                i++;
                            }
                            break;
                        default:
                            matched = false;
                            break;
                    }

                    if (!matched)
                    {
                        cmds.Add(args[i]);
                    }
                }

                args = cmds.ToArray();
                return config;
            }

        }
        public class CommandExecutor(SprdFlashUtils utils, DeviceStatus status)
        {
            private readonly SprdFlashUtils utils = utils;
            private readonly DeviceStatus status = status;
            private CancellationTokenSource cts = new();

            private string commandHelp =
                @"指令帮助
[]内的参数必填, <>内的参数选填
参数指令：
--kick：无fdl刷机（将设备踢入sprd4）
--kickto [0-127]：将设备踢入指定模式(多数设备1为cali_diag, 2为sprd4)
--timeout [毫秒时间]：设置最大超时限制
--wait [秒数]：设置等待设备连接的时间(默认30秒)
运行时指令：
发送并执行fdl：fdl [文件路径] [发送地址（0x格式）]
利用CVE漏洞跳过验证(仅限brom阶段)：exec_addr [文件路径] [发送地址（0x格式）]
写入分区（强制写入在文件路径后加参数force）：w/write_part [分区名] [文件路径] <force>
回读分区：r/read_part [分区名] <保存路径> <回读大小> <回读偏移量>
备份全机: r/read_part all <保存路径>
擦除分区：e/erase_part [分区名]
擦除全机（擦除闪存，慎用）：e/erase_part erase_all
获取分区大小：ps/part_size [分区名]
检查分区是否存在：cp/check_part [分区名]
打印(保存)分区表: partition_list/pl <保存分区表路径>
重新分区：repartition/rp [分区表文件路径]
设置dm-verity：verity [true/false]
设置活动槽位：set_active/sa [a/b]
关机: off/poweroff
开机(至XX模式): rst/reset <fastboot/fb/recovery/rc/factory-reset/fr>

设置块大小：blk_size/bs [大小]
设置最大超时限制: timeout [毫秒时间]";
            private static readonly HashSet<string> CommandKeys = new(StringComparer.OrdinalIgnoreCase)
            {
                "fdl",
                "exec_addr",
                "r","read_part",
                "w","write_part",
                "e","erase_part",
                "ps","part_size"
               ,"cp","check_part",
                "off","poweroff",
                "rst","reset",
                "repartition","rp",
                "partition_list","pl",
                "verity",
                "set_active","sa",
                "blk_size","bs",
                "timeout",
            };


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
            public async Task ExecuteAsync(string command) => await ExecuteAsync(ParseCommand(command));
            public async Task ExecuteAsync(List<string> tokens)
            {
                var subCommands = new List<List<string>>();
                List<string>? current = null;
                foreach (var tok in tokens)
                {
                    if (CommandKeys.Contains(tok))
                    {
                        current = new List<string> { tok };
                        subCommands.Add(current);
                    }
                    else if (current != null)
                    {
                        current.Add(tok);
                    }
                    else
                    {
                        Log(commandHelp);
                        return;
                    }
                }

                foreach (var args in subCommands)
                {
                    bool shouldExit = await ExecuteAsyncSingle(args);
                    if (shouldExit)
                        break;
                }
            }
            public async Task<bool> ExecuteAsyncSingle(List<string> args)
            {
                if (args.Count > 0)
                    switch (args[0])
                    {
                        default: Log(commandHelp); break;
                        case "fdl":
                            if (args.Count < 3 || !File.Exists(args[1]))
                            {
                                Log(commandHelp);
                                return false;
                            }
                            if (status.NowStage >= Stages.Fdl2) break;
                            if (status.NowStage == Stages.Brom) utils.Timeout = 1500;
                            using (FileStream fs = File.OpenRead(args[1]))
                                utils.SendFile(fs, (uint)StringToSize(args[2]));
                            if (status.IsAbleToSendExecAddr && status.ExecAddrFilePath != null)
                                using (FileStream fs = File.OpenRead(status.ExecAddrFilePath))
                                    utils.SendFile(fs, status.ExecAddrSendAddress);
                            utils.ExecuteDataAndConnect(status.NowStage++, status.IsAbleToSendExecAddr);
                            break;
                        case "exec_addr":
                            if (args.Count < 3 || !File.Exists(args[1]))
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (status.NowStage != Stages.Brom)
                            {
                                Log("此命令只能在BROM阶段使用");
                                break;
                            }
                            status.ExecAddrFilePath = args[1];
                            status.ExecAddrSendAddress = (uint)StringToSize(args[2]);
                            break;
                        case "r" or "read_part":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (args[1].ToLowerInvariant() == "all" && status.PartitionList != null)
                            {
                                foreach (var part in status.PartitionList)
                                {
                                    if (part.Name == "userdata" || part.Name == "cache") continue;
                                    using (FileStream fs = File.Create(args.Count >= 3 && Path.Exists(args[2]) ? args[2] : part.Name + ".img"))
                                    {
                                        await utils.ReadPartitionCustomizeAsync(fs, part.Name,
                                            part.Size, cts.Token);
                                    }
                                }
                            }
                            else if (utils.CheckPartitionExist(args[1]))
                                using (FileStream fs = File.Create(args.Count >= 3 ? args[2] : args[1] + ".img"))
                                    await utils.ReadPartitionCustomizeAsync(fs, args[1],
                                       args.Count >= 4 ?
                                       StringToSize(args[3])
                                       : utils.GetPartitionSize(args[1]),
                                        cts.Token
                                        , offset: args.Count >= 5 ? StringToSize(args[4]) : 0);
                            break;
                        case "w" or "write_part":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 3)
                            {
                                break;
                            }
                            if (File.Exists(args[2]))
                                using (FileStream fs = File.OpenRead(args[2]))
                                {
                                    if (args.Count >= 4 && args[3].ToLowerInvariant() == "force" && status.Method != GetPartitionsMethod.TraverseCommonPartitions && status.PartitionList != null)
                                        await utils.WritePartitionWithoutVerifyAsync(args[1], status.PartitionList, fs, cts.Token);
                                    else
                                        await utils.WritePartitionAsync(args[1], fs, cts.Token);
                                }
                            break;
                        case "e" or "erase_part":
                            utils.Timeout += 50000;
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            utils.ErasePartition(args[1]);
                            utils.Timeout -= 50000;
                            break;
                        case "ps" or "part_size":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (utils.CheckPartitionExist(args[1]))
                                Log($"{utils.GetPartitionSize(args[1]) / 1024 / 1024}MB");
                            break;
                        case "cp" or "check_part":
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                return false;
                            }
                            bool exists = utils.CheckPartitionExist(args[1]);
                            Log(exists ? "存在" : "不存在", exists ? ConsoleColor.Green : ConsoleColor.Red);
                            break;
                        case "off" or "poweroff":
                            utils.ShutdownDevice();
                            status.HasExited = true;
                            return true;
                        case "set_active" or "sa":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            utils.SetActiveSlot(args[1] switch
                            {
                                "a" or "A" => SlotToSetActive.SlotA,
                                "b" or "B" => SlotToSetActive.SlotB,
                                _ => throw new ArgumentException("未知的槽位"),
                            });
                            break;
                        case "rst" or "reset":
                            if (args.Count > 2)
                            {
                                try
                                {
                                    utils.ResetToCustomMode(args[1] switch
                                    {
                                        "fastboot" or "fb" => CustomModesToReset.Fastboot,
                                        "recovery" or "rc" or "rec" => CustomModesToReset.Recovery,
                                        "factory-reset" or "fr" => CustomModesToReset.FactoryReset,
                                        _ => throw new ArgumentException("未知的重置模式"),
                                    });
                                }
                                catch (ArgumentException e)
                                {
                                    Log(e.Message);
                                }
                            }
                            utils.PowerOnDevice();
                            status.HasExited = true;
                            return true;
                        case "repartition" or "rp":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (!File.Exists(args[1])) break;
                            var temp = LoadPartitionsXml(File.ReadAllText(args[1]));
                            utils.Repartition(temp);
                            status.PartitionList = temp;
                            break;
                        case "partition_list" or "pl":
                            if (status.NowStage != Stages.Fdl2 || status.PartitionList == null) break;
                            foreach (Partition partition in status.PartitionList)
                            {
                                Log(partition.ToString(), ConsoleColor.Yellow);
                            }
                            if (args.Count >= 2)
                            {
                                using (FileStream fs = File.Create(args[1]))
                                    SavePartitionsToXml(status.PartitionList, fs);
                                Log($"已保存分区表到 {args[1]}");
                            }
                            break;
                        case "verity":
                            if (status.NowStage != Stages.Fdl2) break;
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (status.PartitionList == null || status.Method == GetPartitionsMethod.TraverseCommonPartitions)
                            {
                                Log("当前fdl不支持此功能");
                                break;
                            }
                            if (bool.TryParse(args[1], out bool enable))
                            {
                                utils.SetDmVerityStatus(enable, status.PartitionList);
                            }
                            else
                            {
                                Log("请使用 true 或 false");
                            }
                            break;
                        case "blk_size" or "bs":
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            utils.PerBlockSize = (ushort)StringToSize(args[1]);
                            Log($"已设置块大小为 {utils.PerBlockSize}B");
                            break;
                        case "timeout":
                            if (args.Count < 2)
                            {
                                Log(commandHelp);
                                break;
                            }
                            if (int.TryParse(args[1], out int timeout))
                            {
                                utils.Timeout = timeout;
                                Log($"已设置超时为 {timeout} 毫秒");
                            }
                            else
                            {
                                Log("无效的超时值");
                            }
                            break;

                    }
                return false;
            }

            public void CancelAction()
            {
                cts.Cancel();
                cts = new();
            }
        }
    }
}