using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SPRDClientCore.Protocol
{
    public class SprdProtocolHandler : IDisposable, IProtocolHandler
    {
        public bool Transcode
        {
            get
            {
                return _encoder.UseTranscode;
            }
            set
            {
                _encoder.UseTranscode = value;
            }
        }
        public int Timeout
        {
            get => serialPort.ReadTimeout;
            set
            {
                serialPort.ReadTimeout = value; serialPort.WriteTimeout = value;
            }
        }
        public string PortName => serialPort.PortName;
        public bool Verbose { get; set; }
        public bool UseCrc { get; set; }
        public bool IsPortOpen => serialPort.IsOpen;
        public event Action<string>? Log;


        private byte[] rawPacketBuffer = new byte[0xffff];

        private IEncoder _encoder;
        private bool disposed = false;
        private byte[] lastPacket;
        private SerialPort serialPort = new SerialPort();
        private Crc16Checksum crcChecksum = Crc16Checksum.Instance;
        private SprdChecksum sprdChecksum = SprdChecksum.Instance;
        private const byte HDLC_HEADER = 0x7E;
        private const byte HDLC_ESCAPE = 0x7D;
        private const ushort BSL_REP_LOG = 0x0000;
        private const ushort MAX_READ_LENGTH = 0x8000;

        public SprdProtocolHandler(IEncoder encoder)
        {
            Verbose = true;
            UseCrc = false;
            _encoder = encoder;
            lastPacket = Array.Empty<byte>();
            Timeout = 1000;
        }
        public SprdProtocolHandler(string port, IEncoder encoder)
        {
            Verbose = true;
            UseCrc = false;
            _encoder = encoder;
            lastPacket = Array.Empty<byte>();
            Timeout = 1000;
            serialPort.PortName = port;
            serialPort.Open();
        }

        public static string FindComPort(string identifier = "SPRD U2S DIAG")
        {
            for (; ; )
            {
                var ports = FindAllPorts(identifier);
                foreach (var port in ports)
                {
                    if (!IsPortInUse(port))
                        return port;
                }
                Thread.Sleep(0);
            }
            throw new InvalidOperationException("Find no ports");


            bool IsPortInUse(string portName)
            {
                try
                {
                    using (var port = new SerialPort(portName))
                    {
                        port.Open();
                        port.Close();
                        return false;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
                catch (IOException)
                {
                    return true;
                }
            }

        }
        public static string FindComPort(uint timeout, string Identifier = "SPRD U2S DIAG")
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (; ; )
            {
                if (stopwatch.ElapsedMilliseconds < timeout)
                    foreach (var port in FindAllPorts(Identifier))
                    {
                        return port;
                    }
                else
                {
                    stopwatch.Stop();
                    break;
                }
                Thread.Sleep(0);
            }
            throw new TimeoutException("Find no ports");
        }


        public static List<string> FindAllPorts(string usbIdentifier)
        {
            var ports = new List<string>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)%'"))
            {
                foreach (var obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    if (name != null && name.ToLower().Contains(usbIdentifier.ToLower()))
                    {
                        int start = name.IndexOf("COM");
                        int end = name.IndexOf(')', start);
                        if (start > 3 && end > start)
                        {
                            string port = name.Substring(start, end - start);
                            ports.Add(port);
                        }
                    }
                }
            }
            return ports;
        }
        public bool TryConnectChannel(string port)
        {
            serialPort.PortName = port;
            serialPort.Open();
            return serialPort.IsOpen;
        }
        /*        public bool TryDisconnectChannel()
                {
        *//*            return _bootOpr.DisconnectChannel();
        *//*        
                return serialPort.Dispose();
                }
        */
        public void Dispose()
        {
            try
            {
                /*                _bootOpr?.DisconnectChannel();
                                _bootOpr?.Uninitialize();
                                _bootOpr?.Dispose();
                                _platformApp?.ExitInstance();
                                _platformApp?.Dispose();

                */
                serialPort.Dispose();
            }
            catch (Exception) { }
            disposed = true;
            GC.SuppressFinalize(this);
        }
        private Packet ReceivePacket(byte[] packetBuffer)
        {
            Packet packet;
            while (true)
            {
                try
                {
                    packet = ReceivePacketInternal(packetBuffer);
                }
                catch (ChecksumFailedException)
                {
                    try
                    {
                        packet = SendRetryRequest();
                    }
                    catch (ChecksumFailedException)
                    {
                        UseCrc = !UseCrc;
                        packet = SendRetryRequest();
                    }
                }
                catch (BadPacketException)
                {
                    packet = SendRetryRequest();
                }
                if (packet.Type == BSL_REP_LOG)
                {
                    continue;
                }

                return packet;
            }
        }

        private Packet ReceivePacketInternal(byte[] rawPacketBuffer)
        {
            int Length = ReceiveRawPacket(rawPacketBuffer);
            int writePosition = DecodePacket(rawPacketBuffer, Length);
            ValidateChecksum(rawPacketBuffer.AsMemory(0, writePosition));

            return BuildPacket(writePosition, rawPacketBuffer);
        }
        private int DecodePacket(byte[] rawData, int expectedLength)
        {
            bool _headerFound = false;
            bool _inEscaping = false;
            int nowPosition = 0;
            int writePosition = 0;


            while (true)
            {
                byte currentByte = rawData[nowPosition++];

                if (Transcode)
                {
                    if (_inEscaping)
                    {
                        currentByte ^= 0x20;
                        _inEscaping = false;
                    }
                    else if (currentByte == HDLC_ESCAPE)
                    {
                        _inEscaping = true;
                        continue;
                    }

                    if (currentByte == HDLC_HEADER)
                    {
                        if (!_headerFound)
                        {
                            _headerFound = true;
                            continue;
                        }
                        break;
                    }

                    if (!_headerFound) continue;
                }
                else
                {
                    if (!_headerFound)
                    {
                        if (currentByte == HDLC_HEADER)
                        {
                            _headerFound = true;
                        }
                        continue;
                    }

                    if (writePosition == expectedLength)
                    {
                        if (currentByte != HDLC_HEADER)
                        {
                            throw new BadPacketException("尾0x7e缺失");
                        }
                        break;
                    }
                }

                rawData[writePosition++] = currentByte;

            }

            if (writePosition < 6)
            {
                throw new BadPacketException("响应信息过短");
            }

            if (writePosition != expectedLength)
            {
                throw new BadPacketException(
                    $"长度不一致: 实际 {writePosition} 字节, 预期 {expectedLength} 字节");
            }
            return writePosition;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReceiveRawPacket(byte[] rawPacketBuffer)
        {

            int read = ReadDataWithRetry(rawPacketBuffer, 0, Math.Min(MAX_READ_LENGTH, rawPacketBuffer.Length));
            int expectedLength = BinaryPrimitives.ReadUInt16BigEndian(rawPacketBuffer.AsSpan().Slice(3, 2)) + 6;
            for (int packetLength = read; packetLength < expectedLength; packetLength += read)
                read = ReadDataWithRetry(rawPacketBuffer, packetLength, Math.Min(MAX_READ_LENGTH, rawPacketBuffer.Length - packetLength));
            return expectedLength;


        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int TryReadRawData(byte[] buffer, int offset, int length)
        {
            try { return serialPort.Read(buffer, offset, length); }
            catch (TimeoutException) { return 0; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadDataWithRetry(byte[] buffer, int offset, int length)
        {
            int retryCount = 0;
            int read;

            while ((read = TryReadRawData(buffer, offset, length)) <= 0)
            {
                if (++retryCount > 5)
                {
                    throw new ResponseTimeoutReachedException("响应超时");
                }
                serialPort.Write(lastPacket, 0, lastPacket.Length);
            }
            return read;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateChecksum(ReadOnlyMemory<byte> payload)
        {
            ushort receivedChecksum = BinaryPrimitives.ReadUInt16BigEndian(payload.Span.Slice(payload.Length - 2, 2));
            payload = payload.Slice(0, payload.Length - 2);
            ushort firstCheckSumValue = UseCrc ? crcChecksum.Compute(payload) : sprdChecksum.Compute(payload);
            if (firstCheckSumValue == receivedChecksum)
                return;
            else
            {
                ushort secondCheckSumValue;
                if ((secondCheckSumValue = !UseCrc ? sprdChecksum.Compute(payload) : crcChecksum.Compute(payload)) == receivedChecksum)
                {
                    UseCrc = !UseCrc;
                    return;
                }
                else throw new ChecksumFailedException(
                    $"校验和失败: 接收 0x{receivedChecksum:X4}, 预期 {(UseCrc ? "Crc" : "Sum")} 0x{firstCheckSumValue:X4} 或 {(!UseCrc ? "Crc" : "Sum")} 0x{secondCheckSumValue:X4}");
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Packet BuildPacket(int packetLength, byte[] packetBuffer)
        {
            Packet _recvPacket = new();
            _recvPacket.Type = (SprdCommand)BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(0, 2));
            ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(packetBuffer.AsSpan(2, 2));

            if (packetLength != dataLength + 6)
            {
                throw new BadPacketException(
                    $"长度字段({dataLength})与实际包长度({packetLength})不匹配");
            }

            _recvPacket.Data = new byte[dataLength];
            Buffer.BlockCopy(packetBuffer, 4, _recvPacket.Data, 0, dataLength);
            _recvPacket.ChecksumStrategy = UseCrc ? crcChecksum : sprdChecksum;

            if (Verbose)
            {
                Log?.Invoke($"接收: {_recvPacket.Type}");
            }
            return _recvPacket;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Packet SendRetryRequest()
        {
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            serialPort.Write(lastPacket, 0, lastPacket.Length);
            return ReceivePacketInternal(rawPacketBuffer);
        }
        public async Task SendPacketsAndReceiveAsync(
            ChannelWriter<Packet> receivedPacketsWriter,
            ChannelReader<Packet> packetsToSendReader,
            CancellationToken cancellationToken)
        {
            var encodeAndSendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            var decodeReceivedPacketsChannel = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

            var encodeTask = EncodingPacketsAsync(packetsToSendReader, encodeAndSendChannel.Writer, cancellationToken);
            var sendAndReceiveTask = SendPacketsAndReceiveRawAsync(encodeAndSendChannel.Reader, decodeReceivedPacketsChannel.Writer, cancellationToken);
            var decodeTask = DecodePacketsAsync(decodeReceivedPacketsChannel.Reader, receivedPacketsWriter, cancellationToken);

            await Task.WhenAll(encodeTask, sendAndReceiveTask, decodeTask);
            return;
        }

        private async Task EncodingPacketsAsync(
            ChannelReader<Packet> packetsToSendReader,
            ChannelWriter<byte[]> encodedDataWriter,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var packet in packetsToSendReader.ReadAllAsync(cancellationToken))
                {
                    var encoded = _encoder.Encode(packet);
                    await encodedDataWriter.WriteAsync(encoded, cancellationToken);
                }
            }
            finally
            {
                encodedDataWriter.Complete();
            }
        }
        private async Task SendPacketsAndReceiveRawAsync(ChannelReader<byte[]> encodedDataReader,
            ChannelWriter<(byte[] data, int length)> rawDataWriter,
            CancellationToken cancellationToken)
        {
            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            try
            {
                await foreach (var packetData in encodedDataReader.ReadAllAsync(cancellationToken))
                {
                    serialPort.Write(packetData, 0, packetData.Length);
                    if (Verbose)
                        Log?.Invoke($"[异步]发送{(SprdCommand)packetData[2]}包");
                    lastPacket = packetData;
                    byte[] packetBuffer = pool.Rent(0xffff);
                    int expectedLength = ReceiveRawPacket(packetBuffer);
                    await rawDataWriter.WriteAsync((packetBuffer, expectedLength), cancellationToken);
                }
            }
            finally
            {
                rawDataWriter.Complete();
            }
        }
        private async Task DecodePacketsAsync(
            ChannelReader<(byte[] data, int length)> rawDataReader,
            ChannelWriter<Packet> writer,
            CancellationToken cancellationToken)
        {
            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            try
            {
                await foreach (var packetData in rawDataReader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        int writePosition = DecodePacket(packetData.data, packetData.length);
                        ValidateChecksum(packetData.data.AsMemory(0, writePosition));
                        await writer.WriteAsync(BuildPacket(writePosition, packetData.data), cancellationToken);
                    }
                    finally
                    {
                        pool.Return(packetData.data);
                    }
                }
            }
            finally
            {
                writer.Complete();
            }
        }
        public Packet SendPacketAndReceive(Packet packet) =>
            SendPacketAndReceive(packet.Type, packet.Data, packet.ChecksumStrategy);

        public Packet SendPacketAndReceive(SprdCommand type, IChecksum? checksum = null) =>
            SendPacketAndReceive(type, Array.Empty<byte>(), checksum);
        public Packet SendPacketAndReceive(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum? checksum = null)
        {
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            if (checksum == null)
                checksum = UseCrc ? crcChecksum : sprdChecksum;
            lastPacket = _encoder.Encode(type, data, checksum);
            serialPort.Write(lastPacket, 0, lastPacket.Length);
            if (Verbose) Log?.Invoke($"发送: {type}");
            return ReceivePacket(rawPacketBuffer);
        }
        public byte[] SendPacketAndReceiveBytes(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum? checksum = null)
        {
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            if (checksum == null)
                checksum = UseCrc ? crcChecksum : sprdChecksum;
            lastPacket = _encoder.Encode(type, data, checksum);
            return SendBytesAndReceiveBytes(lastPacket);
        }
        public Packet SendBytesAndReceivePacket(byte[] Data)
        {
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            serialPort.Write(Data, 0, Data.Length);
            return ReceivePacket(rawPacketBuffer);
        }
        public byte[] SendBytesAndReceiveBytes(byte[] Data)
        {
            const int maxDataLength = 65535;
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            serialPort.Write(Data, 0, Data.Length);
            byte[] data = new byte[maxDataLength];
            int dataLength = serialPort.Read(data, 0, data.Length);
            byte[] ret = new byte[dataLength];
            Buffer.BlockCopy(data, 0, ret, 0, dataLength);
            return ret;
        }
    }

}
