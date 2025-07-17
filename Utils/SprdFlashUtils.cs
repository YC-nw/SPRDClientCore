using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml.Linq;
using System.Xml.XPath;
using SPRDClientCore.Models;
using SPRDClientCore.Protocol;
using SPRDClientCore.Protocol.CheckSums;

namespace SPRDClientCore.Utils
{
    public partial class SprdFlashUtils
    {
        private IProtocolHandler handler;
        private static long nowBytes = 0;
        private RequestManager rm;
        public event Action<string>? Log;
        public event Action<int>? UpdatePercentage;
        public event Action<string>? UpdateStatus;
        public ushort PerBlockSize { get; set; } = 0xfe00;
        public int Timeout { get => handler.Timeout; set => handler.Timeout = value; }
        public float Percentage { get; set; } = 100;
        public bool Verbose { get => handler.Verbose; set => handler.Verbose = value; }
        public IProtocolHandler Handler { get => handler; }
        public PartitionManager partitionManager { get; private set; }
        public PartitionManagerSettings Settings { get; set; }
        public SprdFlashUtils(IProtocolHandler handler, Action<string>? log = null, Action<int>? updatePercentage = null, Action<string>? logSpeed = null)
        {
            this.handler = handler;
            Log = log;
            UpdatePercentage = updatePercentage;
            UpdateStatus = logSpeed;
            rm = new(handler);
            Settings = new(handler);
            partitionManager = new PartitionManager(Settings,rm);
        }
        public void PowerOnDevice()
        {
            handler.SendPacketAndReceive(SprdCommand.BSL_CMD_NORMAL_RESET);
        }
        public void ShutdownDevice()
        {
            handler.SendPacketAndReceive(SprdCommand.BSL_CMD_POWER_OFF);
        }
        public void ResetToCustomMode(CustomModesToReset mode)
        {
            if (!CheckPartitionExist("misc")) return;
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] data = Encoding.ASCII.GetBytes("boot-recovery");
                byte[] data2;
                ms.Write(data, 0, data.Length);
                ms.Position = 0x40;
                switch (mode)
                {
                    case CustomModesToReset.FactoryReset:
                        data2 = Encoding.ASCII.GetBytes("recovery\n--wipe_data\n");
                        ms.Write(data2, 0, data2.Length);
                        break;
                    case CustomModesToReset.Fastboot:
                        data2 = Encoding.ASCII.GetBytes("recovery\n--fastboot\n");
                        ms.Write(data2, 0, data2.Length);
                        break;
                }
                ms.SetLength(0x800);
                WritePartition("misc", ms.ToArray(), 0);
            }
        }
        public async Task ResetToCustomModeAsync(CustomModesToReset mode)
        {
            await Task.Run(() =>
            {
                if (!CheckPartitionExist("misc")) return;
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes("boot-recovery");
                    byte[] data2;
                    ms.Write(data, 0, data.Length);
                    ms.Position = 0x40;
                    switch (mode)
                    {
                        case CustomModesToReset.FactoryReset:
                            data2 = Encoding.ASCII.GetBytes("recovery\n--wipe_data\n");
                            ms.Write(data2, 0, data2.Length);
                            break;
                        case CustomModesToReset.Fastboot:
                            data2 = Encoding.ASCII.GetBytes("recovery\n--fastboot\n");
                            ms.Write(data2, 0, data2.Length);
                            break;
                    }
                    ms.SetLength(0x800);
                    WritePartition("misc", ms.ToArray(), 0);
                }
            });
        }
        public void SetDmVerityStatus(bool status, List<Partition> partitions)
        {
            if (!CheckPartitionExist("vbmeta")) return;
            byte[] disableByte = { 1 };
            byte[] enableByte = { 0 };
            switch (status)
            {
                case true:
                    WritePartitionWithoutVerify("vbmeta", partitions, enableByte, 0x7B);
                    Log?.Invoke("已启用DM-verity");
                    break;
                case false:
                    WritePartitionWithoutVerify("vbmeta", partitions, disableByte, 0x7B);
                    Log?.Invoke("已禁用DM-verity");
                    break;
            }
        }
        public (Stages SprdMode, Stages Stage) ConnectToDevice(bool isReconnected = false)
        {
            Packet packet;
            packet = handler.SendPacketAndReceive(isReconnected ? SprdCommand.BSL_CMD_CHECK_BAUD : SprdCommand.BSL_CMD_CONNECT);
            switch (packet.Type)
            {
                default: throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                case SprdCommand.BSL_REP_UNSUPPORTED_COMMAND:
                    handler.Transcode = false;
                    handler.SendPacketAndReceive(SprdCommand.BSL_CMD_DISABLE_TRANSCODE);
                    return (Stages.Sprd3, Stages.Fdl2);
                case SprdCommand.BSL_REP_VERIFY_ERROR:
                    handler.SendPacketAndReceive(SprdCommand.BSL_CMD_CONNECT);
                    if (handler.useCrc)
                        return (Stages.Sprd3, Stages.Brom);
                    else
                        return (Stages.Sprd3, Stages.Fdl1);
                case SprdCommand.BSL_REP_VER:
                    if (!isReconnected) handler.SendPacketAndReceive(SprdCommand.BSL_CMD_CONNECT);
                    if (Encoding.ASCII.GetString(packet.Data).ToLower().Contains("autod"))
                    {
                        if (handler.useCrc) return (Stages.Sprd4, Stages.Brom);
                        else return (Stages.Sprd4, Stages.Fdl1);
                    }
                    else
                    {
                        if (handler.useCrc) return (Stages.Sprd3, Stages.Brom);
                        else return (Stages.Sprd3, Stages.Fdl1);
                    }
                case SprdCommand.BSL_REP_ACK:
                    if (handler.useCrc)
                        return (Stages.Sprd3, Stages.Brom);
                    else
                        return (Stages.Sprd3, Stages.Fdl1);

            }
            throw new ExceptionDefinitions.ResponseTimeoutReachedException("响应超时");
        }
        public static SprdProtocolHandler ChangeDiagnosticMode(Action<string>? log = null, Action<string>? notify = null, ModeOfChangingDiagnostic mode = ModeOfChangingDiagnostic.CommonMode, ModeToChange modeTo = ModeToChange.DlDiagnostic)
        {
            var handler = new SprdProtocolHandler(new HdlcEncoder());
            ChangeDiagnosticMode(handler);
            return handler;
        }
        public static void ChangeDiagnosticMode(SprdProtocolHandler sprdProtocolHandler, Action<string>? log = null, Action<string>? notify = null, ModeOfChangingDiagnostic mode = ModeOfChangingDiagnostic.CommonMode, ModeToChange modeTo = ModeToChange.DlDiagnostic)
        {
            byte[] firstPacket = { 0x7e, 0, 0, 0, 0, 8, 0, 0xfe, 0, 0x7e };
            byte[] autodloaderPacket = { 0x7e, 0, 0, 0, 0, 0x20, 0,
                0x68, 0, 0x41, 0x54, 0x2b, 0x53, 0x50, 0x52, 0x45
                , 0x46, 0x3d, 0x22, 0x41, 0x55, 0x54, 0x4f,
                0x44, 0x4c, 0x4f, 0x41, 0x44, 0x45, 0x52, 0x22,
                0xd, 0xa, 0x7e };
            bool isDeviceConnected = false;
            ComPortMonitor monitor = new("" +
                "", () => isDeviceConnected = false);
            Action waitForDeviceDisconnecting = () =>
            {
                while (isDeviceConnected) continue;
                //   sprdProtocolHandler.TryDisconnectChannel();
                log?.Invoke("等待设备连接中");
            };
            Action connectToDevice = () =>
            {
                monitor.Stop();
                string port = SprdProtocolHandler.FindComPort("SPRD U2S DIAG");
                sprdProtocolHandler.TryConnectChannel(port);
                notify?.Invoke($"成功连接端口{port}");
                monitor = new(port, () => isDeviceConnected = false);
                isDeviceConnected = true;
            };
            connectToDevice();
            switch (mode)
            {
                case ModeOfChangingDiagnostic.CommonMode:
                    firstPacket[8] = 0x81;
                    break;
                case ModeOfChangingDiagnostic.CustomOneTimeMode:
                    firstPacket[8] = (byte)(0x80 + (ushort)modeTo);
                    break;
            }
            log?.Invoke($"尝试发送0x{firstPacket[8]:x}包");
            byte[] bytesReceived1 = sprdProtocolHandler.WriteBytesAndReceiveBytes(firstPacket);
            if (bytesReceived1.Length >= 3)
                if (bytesReceived1[2] == (byte)SprdCommand.BSL_REP_VER || bytesReceived1[2] == (byte)SprdCommand.BSL_REP_VERIFY_ERROR || bytesReceived1[2] == (byte)SprdCommand.BSL_REP_UNSUPPORTED_COMMAND && bytesReceived1[2] != (byte)SprdCommand.BSL_CMD_CHECK_BAUD)
                {
                    monitor.Stop();
                    return; ///TODO:
                }
            waitForDeviceDisconnecting();
            connectToDevice();
            if (modeTo == ModeToChange.DlDiagnostic && mode == ModeOfChangingDiagnostic.CommonMode)
            {
                log?.Invoke($"尝试发送autod包");
                byte[] bytesReceived2 = sprdProtocolHandler.WriteBytesAndReceiveBytes(autodloaderPacket);
                waitForDeviceDisconnecting();
                connectToDevice();
            }
            monitor.Stop();
        }
        public void SendFile(FileStream fileData, uint startAddress, uint perBlockSize = 528, bool sendEndData = true)
        {
            long totalSize = fileData.Length;
            byte[] header = new byte[8];
            byte[] buffer = new byte[0xffff];
            SprdCommand response;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), startAddress);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)totalSize);
            if ((response = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_START_DATA, header).Type) != SprdCommand.BSL_REP_ACK)
                throw new ExceptionDefinitions.UnexceptedResponseException(response);

            bool oldVerbose = handler.Verbose;
            handler.Verbose = false;
            long n = 0;
            for (uint offset = 0; offset < totalSize; offset += perBlockSize)
            {
                n = totalSize - offset;
                if (n > perBlockSize) n = perBlockSize;
                fileData.Position = offset;
                fileData.ReadExactly(buffer, 0, (int)n);
                response = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_MIDST_DATA, buffer.AsMemory(0, (int)n)).Type;
                if (response != SprdCommand.BSL_REP_ACK)
                {
                    throw new ExceptionDefinitions.UnexceptedResponseException(response);
                }

                UpdatePercentage?.Invoke((int)((float)(offset + n) / totalSize * Percentage));
            }
            handler.Verbose = oldVerbose;
            if (sendEndData)
            {
                response = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_END_DATA).Type;
                if (response != SprdCommand.BSL_REP_ACK)
                {
                    throw new ExceptionDefinitions.UnexceptedResponseException(response);
                }
            }
            Log?.Invoke($"成功发送文件至 0x{startAddress:X8}");
        }
        public bool CheckPartitionExist(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName))
                return false;
            var receiveType = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, 0x8)).Type;
            if (receiveType == SprdCommand.BSL_REP_ACK)
            {
                uint[] temp = { 0x8, 0 };
                byte[] tempData = new byte[temp.Length * sizeof(uint)];
                Buffer.BlockCopy(temp, 0, tempData, 0, tempData.Length);
                receiveType = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_MIDST, tempData).Type;
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                return receiveType == SprdCommand.BSL_REP_READ_FLASH;
            }
            else
            {
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                return false;
            }
        }
        private Task StartStatusMonitor(int intervalMs, ulong size, CancellationToken? cancellation = null)
        {
            return Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
                long lastBytes = nowBytes;
                DateTime lastTime = DateTime.UtcNow;
                while (await timer.WaitForNextTickAsync())
                {
                    if (cancellation?.IsCancellationRequested == true)
                    {
                        UpdateStatus?.Invoke("");
                        nowBytes = 0;
                        cancellation?.ThrowIfCancellationRequested();
                    }
                    DateTime now = DateTime.UtcNow;
                    double speed = (nowBytes - lastBytes) / (now - lastTime).TotalSeconds;
                    lastTime = now;
                    UpdateStatus?.Invoke($"速度：{speed / 1024 / 1024.0:N2} MB/s，已完成：{nowBytes / 1024 / 1024.0:N2}MB / {size / 1024 / 1024.0:N2}MB，剩余时间：{(speed != 0 ? ((size - (ulong)nowBytes) / speed).ToString("n2") : "inf")}秒");
                    lastBytes = nowBytes;
                }
            });
        }
        #region 写入分区
        public void WritePartition(string partName, Stream data, ulong offset)
        {
            byte[] buffer = new byte[data.Length];
            data.ReadExactly(buffer, 0, buffer.Length);
            WritePartition(partName, buffer, offset);
        }
        public void WritePartition(string partName, byte[] data, ulong offset)
        {
            if (!CheckPartitionExist(partName)) return;
            using (MemoryStream ms = new())
            {
                ReadPartitionCustomize(ms, partName, GetPartitionSize(partName));
                if (ms == null) return;
                ms.Position = (long)offset;
                ms.Write(data, 0, data.Length);
                ms.Position = 0;
                WritePartition(partName, ms);
            }

        }
        public void WritePartition(string partName, Stream data)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            byte[] buffer = new byte[0xffff];
            try
            {
                if (partName.Contains("runtimenv"))
                { ErasePartition(partName); return; }
                if (partName.Contains("calinv")) return;
                if (partName.Contains("fixnv1"))
                {
                    uint offset = 0;
                    uint length = 4;
                    byte[] dataBytes = new byte[data.Length];
                    data.ReadExactly(dataBytes, 0, dataBytes.Length);
                    if (dataBytes.Length >= 4 && BitConverter.ToUInt32(dataBytes, 0) == 0x4e56)
                    {
                        offset += 0x200;
                    }
                    ushort[] _temp = { 0, 0 };
                    while (true)
                    {
                        _temp[0] = BitConverter.ToUInt16(dataBytes, (int)(offset + length));
                        _temp[1] = BitConverter.ToUInt16(dataBytes, (int)(offset + length + sizeof(ushort)));
                        if (_temp[1] == 0) throw new Exception("损坏的NV文件");
                        length += _temp[1] + sizeof(ushort) * (uint)_temp.Length;
                        uint tempOffset = (length + 3 & 0xFFFFFFFC) - length;
                        length += tempOffset;
                        _temp[0] = BitConverter.ToUInt16(dataBytes, (int)(offset + length));
                        if (_temp[0] == 0xffff)
                        {
                            length += 8;
                            break;
                        }
                    }
                    byte[] tempBytes = new byte[length - sizeof(ushort)];
                    Buffer.BlockCopy(dataBytes, (int)offset + 2, tempBytes, 0, tempBytes.Length);
                    byte[] crc = BitConverter.GetBytes(new Crc16NvChecksum().Compute(tempBytes));
                    Array.Reverse(crc);
                    Buffer.BlockCopy(crc, 0, dataBytes, (int)offset, crc.Length);
                    byte[] startData = new byte[36 * sizeof(char) + 2 * sizeof(uint)];
                    Buffer.BlockCopy(Encoding.Unicode.GetBytes(partName), 0, startData, 0, partName.Length * sizeof(char));
                    Buffer.BlockCopy(BitConverter.GetBytes(length), 0, startData, 36 * sizeof(char), sizeof(int));
                    uint checksum = 0;
                    for (int i = (int)offset; i < offset + length; i++)
                        checksum += dataBytes[i];
                    Buffer.BlockCopy(BitConverter.GetBytes(checksum), 0, startData, 36 * sizeof(char) + sizeof(int), sizeof(int));
                    Packet packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_START_DATA, startData);
                    if (packet.Type != SprdCommand.BSL_REP_ACK) throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                    for (uint nowOffset = 0; nowOffset < length;)
                    {
                        uint nowSize = Math.Min(PerBlockSize, length - nowOffset);
                        byte[] block = new byte[nowSize];
                        Buffer.BlockCopy(dataBytes, (int)nowOffset, block, 0, block.Length);
                        packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_MIDST_DATA, block);
                        if (packet.Type != SprdCommand.BSL_REP_ACK)
                            throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                        nowOffset += nowSize;
                        UpdatePercentage?.Invoke((int)(nowOffset / (float)length * Percentage));
                    }
                    packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_END_DATA);
                    if (packet.Type != SprdCommand.BSL_REP_ACK)
                        throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);

                    return;
                }
                ulong dataLength = (ulong)data.Length;
                var temp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_START_DATA, CreateSelectPartitionRequest(partName, dataLength)).Type;
                if (temp != SprdCommand.BSL_REP_ACK) throw new ExceptionDefinitions.UnexceptedResponseException(temp);
                StartStatusMonitor(1000, dataLength, cts.Token);
                for (ulong nowOffset = 0; nowOffset < dataLength;)
                {
                    ulong nowSize = Math.Min(PerBlockSize, dataLength - nowOffset);
                    data.Position = (long)nowOffset;
                    data.ReadExactly(buffer, 0, (int)nowSize);
                    //Buffer.BlockCopy(data., (int)nowOffset, chunk, 0, (int)nowSize);
                    temp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_MIDST_DATA, buffer.AsMemory(0, (int)nowSize)).Type;
                    if (temp != SprdCommand.BSL_REP_ACK)
                        throw new ExceptionDefinitions.UnexceptedResponseException(temp);
                    nowBytes += (long)nowSize;
                    nowOffset += nowSize;
                    UpdatePercentage?.Invoke((int)(nowOffset / (float)dataLength * Percentage));
                }
                var __temp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_END_DATA).Type;
                if (__temp != SprdCommand.BSL_REP_ACK)
                    throw new ExceptionDefinitions.UnexceptedResponseException(__temp);
            }
            finally
            {
                cts.Cancel();
                data.Dispose();
            }
        }
        public async Task WritePartitionAsync(string partName, Stream data, CancellationToken token)
        {
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            token = cts.Token;
            Task? sendAndReceiveTask = null;
            Task? sendTask = null;

            ushort blocksize = PerBlockSize;
            ulong dataLength = (ulong)data.Length;
            bool use64Mode = dataLength >> 32 != 0;

            var StatusMonitor = StartStatusMonitor(500, dataLength, token);

            Channel<Packet> sendChannel = Channel.CreateBounded<Packet>(new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait });
            Channel<Packet> receiveChannel = Channel.CreateUnbounded<Packet>();

            Stopwatch sw = new();
            sw.Start();
            try
            {
                if (partName.Contains("runtimenv"))
                { ErasePartition(partName); return; }
                if (partName.Contains("calinv")) return;
                if (partName.Contains("fixnv1"))
                {
                    WritePartition(partName, data);
                    return;
                }

                var temp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_START_DATA, CreateSelectPartitionRequest(partName, dataLength)).Type;
                if (temp != SprdCommand.BSL_REP_ACK) throw new ExceptionDefinitions.UnexceptedResponseException(temp);

                sendAndReceiveTask = handler.SendPacketsAndReceiveAsync(
                    receiveChannel.Writer,
                    sendChannel.Reader,
                    token
                    );

                var checksum = SprdChecksum.Instance;

                sendTask = Task.Run(async () =>
                {
                    for (ulong i = 0; i < dataLength;)
                    {
                        data.Position = (long)i;
                        token.ThrowIfCancellationRequested();
                        ushort nowReadSize = (ushort)Math.Min(dataLength - i, blocksize);
                        byte[] block = new byte[nowReadSize];
                        data.ReadExactly(block, 0, block.Length);
                        await sendChannel.Writer.WriteAsync(new Packet(SprdCommand.BSL_CMD_MIDST_DATA, block, checksum));
                        i += nowReadSize;
                    }
                    sendChannel.Writer.Complete();
                }, token);

                int packetCounts = 0;
                int expectedPacketsCounts = (int)Math.Ceiling((double)dataLength / blocksize);

                await foreach (var packet in receiveChannel.Reader.ReadAllAsync(token))
                {
                    if (packet.Type != SprdCommand.BSL_REP_ACK)
                        throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                    packetCounts++;
                    UpdatePercentage?.Invoke(packetCounts == expectedPacketsCounts ? (int)Percentage : (int)(packetCounts * blocksize / (float)dataLength * Percentage));
                    nowBytes += blocksize;
                    if (packetCounts >= expectedPacketsCounts)
                        break;
                }
            }
            finally
            {
                sw.Stop();
                Log?.Invoke($"耗时{sw.Elapsed.ToString("g")}");
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_END_DATA);
                receiveChannel.Writer.Complete();
                cts.Cancel();
                cts.Dispose();
                try
                {
                    await StatusMonitor;
                }
                catch (OperationCanceledException)
                {

                }

                if (sendAndReceiveTask != null)
                    try
                    {
                        await sendAndReceiveTask;
                    }
                    catch (OperationCanceledException)
                    {

                    }
                if (sendTask != null)
                    try
                    {
                        await sendTask;
                    }
                    catch (OperationCanceledException)
                    {

                    }

            }
        }
        public async Task WritePartitionWithoutVerifyAsync(string partName, List<Partition> partitions, Stream data, CancellationToken token)
        {
            if (partName == "splloader" || partName == "ubipac" || partName == "sml") return;
            const string forceName = "temp_part";

            List<Partition> temp = partitions.ToList();

            int index = temp.FindIndex(p => p.Name == partName);

            if (index < 0)
            {
                Log?.Invoke($"分区{partName}不存在，无法写入");
                return;
            }

            var updatedPartition = temp[index];
            updatedPartition.Name = forceName;
            temp[index] = updatedPartition;

            try
            {
                Repartition(temp);
                bool a = CheckPartitionExist(forceName);
                await WritePartitionAsync(forceName, data, token);
            }
            finally
            {
                Repartition(partitions);
            }
        }
        public void WritePartitionWithoutVerify(string partName, List<Partition> partitions, byte[] data, ulong offset)
        {
            const string forceName = "skip_verify";

            List<Partition> temp = partitions.ToList();

            int index = temp.FindIndex(p => p.Name == partName);

            if (index < 0)
            {
                Log?.Invoke($"分区{partName}不存在，无法写入");
                return;
            }

            var updatedPartition = temp[index];
            updatedPartition.Name = forceName;
            temp[index] = updatedPartition;

            Repartition(temp);

            WritePartition(forceName, data, offset);

            Repartition(partitions);
        }
        public void WritePartitionWithoutVerify(string partName, List<Partition> partitions, Stream data, ulong offset = 0)
        {
            const string forceName = "tempname";

            List<Partition> temp = partitions.ToList();

            int index = temp.FindIndex(p => p.Name == partName);

            if (index < 0)
            {
                Log?.Invoke($"分区{partName}不存在，无法写入");
                return;
            }

            var updatedPartition = temp[index];
            updatedPartition.Name = forceName;
            temp[index] = updatedPartition;

            Repartition(temp);

            if (offset == 0)
                WritePartition(forceName, data);
            else
                WritePartition(forceName, data, offset);

            Repartition(partitions);
        }
        #endregion
        #region 读取分区
        byte[] CreateReadPartitionRequest(uint nowReadSize,ulong nowReadOffset,bool useMode64)
        {
            byte[] result = new byte[useMode64 ? 12 : 8];
            CreateReadPartitionRequest(result, nowReadSize, nowReadOffset, useMode64);
            return result;
        }
        void CreateReadPartitionRequest(Memory<byte> buffer, uint nowReadSize, ulong nowReadOffset, bool use64Mode)
        {
            if (buffer.Length < 8) throw new ArgumentException();
            if (use64Mode && buffer.Length < 12) throw new ArgumentException();
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span, nowReadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span.Slice(4), (uint)(nowReadOffset & 0xffffffff));
            if (use64Mode) BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span.Slice(8), (uint)(nowReadOffset >> 32));
        }
        public void ReadPartitionCustomize(Stream partDataStream, string partName, ulong size, ulong offset = 0)
        {
            if (!CheckPartitionExist(partName)) return;
            if (partName == "splloader" || partName == "ubipac" || partName == "uboot" || partName == "sml" | partName == "trustos")
                PerBlockSize = 0x1000;
            if (partName.Contains("fixnv1")) partName = partName.Replace('1', '2');
            ushort originBlockSize = PerBlockSize;
            bool useMode64 = size >> 32 != 0;
            Packet readPacket = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, (uint)size));
            CancellationTokenSource cts = new CancellationTokenSource();
            try
            {

                if (readPacket.Type != SprdCommand.BSL_REP_ACK)
                    throw new ExceptionDefinitions.UnexceptedResponseException(readPacket.Type);
                Log?.Invoke($"开始读取{partName}分区");
                StartStatusMonitor(500, size, cts.Token);
                byte[] tempData = new byte[useMode64 ? 12 : 8];
                for (ulong nowOffset = offset; nowOffset < size;)
                {
                    uint nowReadSize = (uint)Math.Min(size - nowOffset, PerBlockSize);
                    CreateReadPartitionRequest(tempData, nowReadSize, nowOffset, useMode64);
                    readPacket = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_MIDST, tempData);
                    nowBytes += nowReadSize;
                    if (readPacket.Type != SprdCommand.BSL_REP_READ_FLASH) throw new ExceptionDefinitions.UnexceptedResponseException(readPacket.Type);
                    partDataStream.WriteAsync(readPacket.Data, 0, readPacket.Data.Length);
                    nowOffset += nowReadSize;
                    UpdatePercentage?.Invoke((int)(nowOffset / (float)size * Percentage));
                }
            }
            finally
            {
                cts.Cancel();
                cts.Dispose();
                PerBlockSize = originBlockSize;
                readPacket = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                if (readPacket.Type != SprdCommand.BSL_REP_ACK) throw new ExceptionDefinitions.UnexceptedResponseException(readPacket.Type);
                Log?.Invoke($"{partName}分区读取完毕");
            }
        }
        public async Task ReadPartitionCustomizeAsync(Stream partDataStream, string partName, ulong size, CancellationToken ct, ulong offset = 0)
        {
            if (!CheckPartitionExist(partName))
                return;

            bool useMode64 = size >> 32 != 0;
            ushort originBlockSize = PerBlockSize;
            if (partName == "splloader" || partName == "ubipac" ||
                partName == "uboot" || partName == "sml" ||
                partName == "trustos")
            {
                PerBlockSize = 0x1000;
            }
            if (partName.Contains("fixnv1"))
                partName = partName.Replace('1', '2');
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = cts.Token;
            var statusMonitor = StartStatusMonitor(500, size, ct);
            var sendChannel = Channel.CreateBounded<Packet>(new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait });
            var receiveChannel = Channel.CreateUnbounded<Packet>();
            Task? sendReadPacketsTask = null;
            Task? sendAndReceiveTask = null;
            Stopwatch sw = new();
            sw.Start();
            try
            {


                var ack = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, size + offset));
                if (ack.Type != SprdCommand.BSL_REP_ACK)
                    throw new ExceptionDefinitions.UnexceptedResponseException(ack.Type);
                Log?.Invoke($"开始读取{partName}分区（SPRDClient极速异步读取中）");
                sendAndReceiveTask = handler.SendPacketsAndReceiveAsync(
    receiveChannel.Writer,
    sendChannel.Reader,
    cts.Token
);
                IChecksum checksum = SprdChecksum.Instance;

                sendReadPacketsTask = Task.Run(async () =>
                {
                    for (ulong i = offset; i < size + offset;)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint nowReadSize = (uint)Math.Min(PerBlockSize, size + offset - i);
                        await sendChannel.Writer.WriteAsync(new Packet(SprdCommand.BSL_CMD_READ_MIDST, CreateReadPartitionRequest(nowReadSize,i,useMode64), checksum), ct);
                        i += nowReadSize;
                    }
                    sendChannel.Writer.Complete();
                }, ct);

                await foreach (var packet in receiveChannel.Reader.ReadAllAsync(ct))
                {
                    if (packet.Type != SprdCommand.BSL_REP_READ_FLASH)
                        throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                    partDataStream.Write(packet.Data);
                    nowBytes += packet.Data.LongLength;
                    UpdatePercentage?.Invoke((int)(nowBytes / (float)size * Percentage));
                    if ((ulong)nowBytes >= size)
                        break;
                }
                if (ct.IsCancellationRequested)
                    UpdatePercentage?.Invoke(0);
            }
            finally
            {
                sw.Stop();
                Log?.Invoke($"耗时{sw.Elapsed.ToString("g")}");
                cts.Cancel();
                cts.Dispose();
                receiveChannel.Writer.Complete();

                try
                {
                    await statusMonitor;
                }
                catch (OperationCanceledException) { }

                if (sendReadPacketsTask != null)
                    try
                    {
                        await sendReadPacketsTask;
                    }
                    catch (OperationCanceledException) { }
                if (sendAndReceiveTask != null)
                    try
                    {

                        await sendAndReceiveTask;
                    }
                    catch (OperationCanceledException) { }

                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                PerBlockSize = originBlockSize;
                Log?.Invoke($"读取{partName}分区结束,大小：{size},偏移量:{offset}");
            }

        }
        #endregion
        public void ErasePartition(string partName, int timeout = 20000)
        {
            int originTimeout = handler.Timeout;
            handler.Timeout = timeout;
            handler.SendPacketAndReceive(SprdCommand.BSL_CMD_ERASE_FLASH, CreateSelectPartitionRequest(partName, 0));
            handler.Timeout = originTimeout;
        }
        public ulong GetPartitionSize(string partName)
        {
            if (partName.Contains("nv1") || partName.Contains("nv2"))
            {
                partName = partName.Replace('1', '2');
                Packet packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, 8));

                if (packet.Type != SprdCommand.BSL_REP_ACK) throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);

                uint[] tempData = { 8, 0 };
                byte[] data = new byte[tempData.Length * sizeof(uint)];
                Buffer.BlockCopy(tempData, 0, data, 0, data.Length);
                packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_MIDST, data);
                if (packet.Type != SprdCommand.BSL_REP_READ_FLASH) throw new ExceptionDefinitions.UnexceptedResponseException(packet.Type);
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                uint _temp = BinaryPrimitives.ReadUInt32LittleEndian(packet.Data.AsSpan());
                if (_temp != 0x00004e56)
                    throw new ExceptionDefinitions.BadPacketException("错误的响应");
                return BinaryPrimitives.ReadUInt32LittleEndian(packet.Data.AsSpan(4));
            }
            int end = 20;
            ulong offset = 0;
            bool incrementing = true;
            var temp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, 0xffffffff)).Type;
            bool nandMode = false;
            if (temp != SprdCommand.BSL_REP_ACK)
                nandMode = true;
            if (nandMode)
            {
#if false
                end = 6;
                _sprdProtocolHandler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                for (int i = 21; i >= end;)
                {
                    UInt64 trial = offset + (1UL << i) - (1UL << end);
                    var ret = _sprdProtocolHandler.SendPacketAndReceive(SelectPartition(partName, (uint)trial, SprdCommand.BSL_CMD_READ_START)).Type;
                    if (incrementing)
                    {
                        if (ret != SprdCommand.BSL_REP_ACK)
                        {
                            offset += 1UL << (i - 1);
                            i -= 2;
                            incrementing = false;
                        }
                        else i++;
                    }
                    else
                    {
                        if (ret == SprdCommand.BSL_REP_ACK) offset += (1UL << i);
                        i--;
                    }
                    _sprdProtocolHandler.SendPacketAndReceive(SelectPartition(partName, (uint)trial, SprdCommand.BSL_CMD_READ_END));
                }
                offset -= (1UL << end);
#endif
#if true
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                bool validNandSize(string partName, int validSize)
                {
                    SprdCommand ret = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_START, CreateSelectPartitionRequest(partName, (ulong)validSize * 128UL * 1024UL)).Type;
                    handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
                    return ret == SprdCommand.BSL_REP_ACK;
                }
                ;
                int low = 1, high = 1;
                while (validNandSize(partName, high))
                {
                    low = high;
                    high *= 2;
                }
                int i_min = high;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2;
                    if (validNandSize(partName, mid))
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        i_min = mid;
                        high = mid - 1;
                    }
                }
                offset = (ulong)(i_min - 1) * 128 * 1024;
#endif
            }
            else
            {
                byte[] data = new byte[3 * sizeof(uint)];
                for (int i = end; i >= end;)
                {
                    ulong trial = offset + (1UL << i) - (1UL << end);
                    uint low = (uint)trial;
                    uint high = (uint)(trial >> 32);
                    Buffer.BlockCopy(new uint[] { 4, low, high }, 0, data, 0, data.Length);

                    var resp = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_MIDST, data).Type;

                    if (incrementing)
                    {
                        if (resp != SprdCommand.BSL_REP_READ_FLASH)
                        {
                            if (trial == 0) break;
                            offset += 1UL << i - 1;
                            i -= 2;
                            incrementing = false;
                        }
                        else
                        {
                            i = i == 10 ? i + 11 : i + 1;
                        }
                    }
                    else
                    {
                        if (resp == SprdCommand.BSL_REP_READ_FLASH)
                            offset += 1UL << i;
                        i--;
                    }
                }
                handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_END);
            }

            return offset;
        }
        public (List<Partition> partitions, GetPartitionsMethod finalMethod) GetPartitionsAndStorageInfo(GetPartitionsMethod method = GetPartitionsMethod.ConvertExtTable, Action<string>? SpecifiedLog = null, Func<Task<bool>>? CheckConfirm = null)
        {
            List<Partition> partList = new List<Partition>();
            if (CheckPartitionExist("ubipac"))
            {
                Log?.Invoke("检测到设备为nand机型");
                SpecifiedLog?.Invoke("检测到设备为nand机型");
                method = GetPartitionsMethod.TraverseCommonPartitions;
            }
            switch (method)
            {
                default: throw new ArgumentException();
                case GetPartitionsMethod.ConvertExtTable:
                    if (!CheckPartitionExist("user_partition"))
                    {
                        if (CheckConfirm != null)
                            if (!CheckConfirm.Invoke().Result)
                                goto case GetPartitionsMethod.SendReadPartitionPacket;
                            else
                                goto case GetPartitionsMethod.TraverseCommonPartitions;

                        goto case GetPartitionsMethod.SendReadPartitionPacket;
                    }
                    Log?.Invoke("尝试使用方法一获取分区表");
                    SpecifiedLog?.Invoke("尝试使用方法一获取分区表");
                    using (MemoryStream? ms = new MemoryStream())
                    {
                        ReadPartitionCustomize(ms, "user_partition", 32 * 1024);
                        if (ms != null)
                        {
                            EfiTableUtils efiTableUtils = new EfiTableUtils();
                            var tmp = efiTableUtils.GetPartitions(ms);
                            tmp.partitions.Insert(0, new Partition() { Name = "splloader", Size = GetPartitionSize("splloader"), IndicesToMB = 20 });
                            return (tmp.partitions, GetPartitionsMethod.ConvertExtTable);
                        }
                        else
                        {
                            Log?.Invoke("方法一获取失败");
                            SpecifiedLog?.Invoke("方法一获取失败");
                            goto case GetPartitionsMethod.SendReadPartitionPacket;
                        }
                    }
                case GetPartitionsMethod.SendReadPartitionPacket:
                    Log?.Invoke("尝试使用方法二获取分区表");
                    SpecifiedLog?.Invoke("尝试使用方法二获取分区表");
                    partList.Clear();
                    Packet packet = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_READ_PARTITION);
                    if (packet.Type == SprdCommand.BSL_REP_READ_PARTITION)
                    {
                        byte[] partNameBytes = new byte[36 * sizeof(char)],
                            partSizeBytes = new byte[sizeof(uint)];
                        int indices = 10; // 1024
                        int partCount = packet.Data.Length / (partNameBytes.Length + partSizeBytes.Length);
                        for (int i = 0; i < packet.Data.Length; i += partNameBytes.Length + partSizeBytes.Length)
                        {
                            while (BinaryPrimitives.ReadUInt32LittleEndian(packet.Data.AsSpan(i + partNameBytes.Length, partSizeBytes.Length)) >> indices == 0)
                                indices--;
                        }
                        partList.Add(new Partition() { Name = "splloader", Size = GetPartitionSize("splloader"), IndicesToMB = 20 });
                        for (int i = 0; i < packet.Data.Length; i += partNameBytes.Length + partSizeBytes.Length)
                        {
                            partList.Add(new Partition
                            {
                                Name = Encoding.Unicode.GetString(packet.Data, i, partNameBytes.Length),
                                Size = BinaryPrimitives.ReadUInt32LittleEndian(packet.Data.AsSpan(i + partNameBytes.Length, partSizeBytes.Length)),
                                IndicesToMB = indices
                            });

                        }
                        return (partList, GetPartitionsMethod.SendReadPartitionPacket);
                    }
                    else
                    {
                        Log?.Invoke($"方法二获取失败：{packet.Type.ToString()}");
                        SpecifiedLog?.Invoke($"方法二获取失败：{packet.Type.ToString()}");
                        goto case GetPartitionsMethod.TraverseCommonPartitions;
                    }
                case GetPartitionsMethod.TraverseCommonPartitions:
                    Log?.Invoke("使用兼容性方法获取分区表");
                    SpecifiedLog?.Invoke("使用兼容性方法获取分区表");
                    partList.Clear();
                    foreach (string partName in Partition.CommonPartitions)
                    {
                        if (CheckPartitionExist(partName))
                        {
                            partList.Add(new Partition { Name = partName, Size = GetPartitionSize(partName), IndicesToMB = 20 });
                        }
                    }
                    return (partList, GetPartitionsMethod.TraverseCommonPartitions);
            }
        }
        public void Repartition(List<Partition> partitions)
        {
            byte[] repartitionData = new byte[partitions.Count * (36 * sizeof(char) + sizeof(uint))];
            int i = 0;
            foreach (Partition partition in partitions)
            {
                if (partition.Name.Length > 35)
                {
                    Log?.Invoke($"分区{partition.Name}名称过长，无法重新分区");
                    continue;
                }

                var tempPartNameChunk = Encoding.Unicode.GetBytes(partition.Name);
                var tempPartSizeChunk = BitConverter.GetBytes((uint)(partition.Name == "userdata" ? 0xffffffff : partition.Size / (1UL << partition.IndicesToMB)));

                Buffer.BlockCopy(tempPartNameChunk, 0, repartitionData, i, tempPartNameChunk.Length);
                Buffer.BlockCopy(tempPartSizeChunk, 0, repartitionData, i + 36 * sizeof(char), tempPartSizeChunk.Length);
                i += 36 * sizeof(char) + sizeof(uint);
            }
            var a = handler.SendPacketAndReceive(SprdCommand.BSL_CMD_REPARTITION, repartitionData).Type;
            if (a != SprdCommand.BSL_REP_ACK)
            {
                throw new ExceptionDefinitions.UnexceptedResponseException(a);
            }
        }
        private byte[] CreateSelectPartitionRequest(string partName,
                                                    ulong size,
                                                    IChecksum? checksum = null)
        {
            bool useMode64 = size >> 32 != 0;
            byte[] result = new byte[useMode64 ? 80 : 76];
            CreateSelectPartitionRequest(result,partName, size, checksum);
            return result;
        }
        private void CreateSelectPartitionRequest(Memory<byte> partData, string partName, ulong size, IChecksum? checksum = null)
        {
            bool useMode64 = size >> 32 != 0;
            if (partData.Length < 76) throw new ArgumentException();
            if (useMode64 && partData.Length < 80) throw new ArgumentException();
            Encoding.Unicode.GetBytes(partName).CopyTo(partData.Span.Slice(0, 72));
            BinaryPrimitives.WriteUInt32LittleEndian(partData.Span.Slice(72, 4), (uint)size);
            if (useMode64) BinaryPrimitives.WriteUInt32LittleEndian(partData.Span.Slice(76, 4), (uint)(size >> 32));
        }


        public bool SendAndCheck(SprdCommand sendType, SprdCommand expectedType = SprdCommand.BSL_REP_ACK)
        {
            Packet res = handler.SendPacketAndReceive(sendType);
            if (res.Type == SprdCommand.BSL_REP_VERIFY_ERROR)
            {
                handler.useCrc = !handler.useCrc;
                res = handler.SendPacketAndReceive(sendType);
            }
            return res.Type == expectedType;
        }
        public DaInfo? ExecuteDataAndConnect(Stages stage)
        {
            switch (stage)
            {
                default: throw new ArgumentException();
                case Stages.Brom:
                    SendAndCheck(SprdCommand.BSL_CMD_EXEC_DATA);
                    handler.useCrc = false;
                    SendAndCheck(SprdCommand.BSL_CMD_CHECK_BAUD);
                    SendAndCheck(SprdCommand.BSL_CMD_CONNECT);
                    SendAndCheck(SprdCommand.BSL_CMD_KEEP_CHARGE);
                    return null;
                case Stages.Fdl1:
                    DaInfo temp = GetDaInfo(handler.SendPacketAndReceive(SprdCommand.BSL_CMD_EXEC_DATA).Data);
                    SendAndCheck(SprdCommand.BSL_CMD_DISABLE_TRANSCODE);
                    handler.Transcode = false;
                    return temp;
            }
        }
        public static DaInfo GetDaInfo(byte[] data)
        {
            var daInfo = new DaInfo();

            if (data.Length > 6)
            {
                if (data.Length >= 4 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) == 0x7477656E)
                {
                    int len = 4;
                    while (len + 4 <= data.Length) // 确保可以读取Type和Length
                    {
                        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(len, 2));
                        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(len + 2, 2));
                        len += 4; // 移动过Type和Length

                        // 根据Type处理Value，并跳过Length字节
                        switch (type)
                        {
                            case 0: // bDisableHDLC (uint)
                                if (len + 4 <= data.Length)
                                    daInfo.bDisableHDLC = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(len, 4));
                                len += length; // 跳过Length字节
                                break;
                            case 2: // bSupportRawData (byte)
                                if (len < data.Length)
                                    daInfo.bSupportRawData = data[len];
                                len += length;
                                break;
                            case 3: // dwFlushSize (uint)
                                if (len + 4 <= data.Length)
                                    daInfo.dwFlushSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(len, 4));
                                len += length;
                                break;
                            case 6: // dwStorageType (uint)
                                if (len + 4 <= data.Length)
                                    daInfo.dwStorageType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(len, 4));
                                len += length;
                                break;
                            default:
                                len += length; // 跳过未知类型的Value
                                break;
                        }
                    }
                }
                else
                {
                    int offset = 0;
                    if (offset + 4 <= data.Length)
                    {
                        daInfo.dwVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                        offset += 4;
                    }
                    if (offset + 4 <= data.Length)
                    {
                        daInfo.bDisableHDLC = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                        offset += 4;
                    }
                    if (offset < data.Length)
                    {
                        daInfo.bIsOldMemory = data[offset++];
                    }
                    if (offset < data.Length)
                    {
                        daInfo.bSupportRawData = data[offset++];
                    }
                    // 预留bReserve[2]
                    if (offset + 2 <= data.Length)
                    {
                        daInfo.bReserve = new byte[2] { data[offset], data[offset + 1] };
                        offset += 2;
                    }
                    if (offset + 4 <= data.Length)
                    {
                        daInfo.dwFlushSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                        offset += 4;
                    }
                    if (offset + 4 <= data.Length)
                    {
                        daInfo.dwStorageType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                        offset += 4;
                    }
                    // 预留dwReserve[59]
                    daInfo.dwReserve = new uint[59];
                    for (int i = 0; i < 59 && offset + 4 <= data.Length; i++, offset += 4)
                        daInfo.dwReserve[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                }
            }

            return daInfo;
        }
        private static byte[] StructToBytes<T>(T obj, int size) where T : struct
        {
            //int size = Marshal.SizeOf(obj);
            byte[] bytes = new byte[size];
            nint ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(obj, ptr, false);
                Marshal.Copy(ptr, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return bytes;
        }
        public static ulong StringToSize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPart = s.Substring(2);
                if (uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexValue))
                    return hexValue;
                throw new ArgumentException($"无法解析十六进制数：{s}", nameof(s));
            }

            var units = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
    {
        { "B", 1UL },
        { "",  1UL },
        { "K", 1024UL },
        { "KB", 1024UL },
        { "M", 1024UL * 1024 },
        { "MB", 1024UL * 1024 },
        { "G", 1024UL * 1024 * 1024 },
        { "GB", 1024UL * 1024 * 1024 },
        { "T", 1024UL * 1024 * 1024 * 1024 },
        { "TB", 1024UL * 1024 * 1024 * 1024 },
    };

            var match = Regex.Match(s, @"^(?<num>\d+)(?<unit>[a-zA-Z]{0,2})$");
            if (!match.Success)
                throw new ArgumentException($"格式不正确：{s}", nameof(s));

            string numPart = match.Groups["num"].Value;
            string unitPart = match.Groups["unit"].Value.ToUpperInvariant();

            if (!ulong.TryParse(numPart, out ulong number))
                throw new ArgumentException($"无法解析数字部分：{numPart}", nameof(s));
            if (!units.TryGetValue(unitPart, out ulong multiplier))
                throw new ArgumentException($"不支持的单位：{unitPart}", nameof(s));

            ulong result = number * multiplier;
            if (result > ulong.MaxValue)
                throw new OverflowException($"结果超过最大值：{result}");

            return result;
        }
        public static void SavePartitionsToXml(List<Partition> partitions, Stream stream)
        {
            var doc = new XDocument(
                new XElement("Partitions",
                    partitions.Select(p =>
                        new XElement("Partition",
                            new XAttribute("id", p.Name),
                            new XAttribute("size", p.Name == "userdata" ? "0xFFFFFFFF" : Math.Ceiling(p.Size / (double)(1 << p.IndicesToMB)))
                        )
                    )
                )
            );

            doc.Save(stream);
        }
        public static List<Partition> LoadPartitionsXml(string xmlContent)
        {
            Func<string, ulong> ParseSize = (sizeString) =>
            {
                if (sizeString.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToUInt64(sizeString.Substring(2), 16);
                }
                return Convert.ToUInt64(sizeString);
            };
            var partitions = new List<Partition>();
            var doc = XDocument.Parse(xmlContent);

            var nodes = doc.XPathSelectElements("//Partitions/Partition");

            foreach (var node in nodes)
            {
                var nameAttr = node.Attribute("id");
                var sizeAttr = node.Attribute("size");

                if (nameAttr == null || sizeAttr == null) continue;

                try
                {
                    var partition = new Partition
                    {
                        Name = nameAttr.Value,
                        Size = ParseSize(sizeAttr.Value),
                        IndicesToMB = 0
                    };
                    partitions.Add(partition);
                }
                catch (FormatException)
                {
                }
            }

            return partitions;
        }
    }
}
