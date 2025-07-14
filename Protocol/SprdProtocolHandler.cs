using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SPRDClientCore.Models;
using SPRDClientCore.Protocol.CheckSums;
using SPRDClientCore.Protocol.Encoders;

namespace SPRDClientCore.Protocol
{
    public interface IProtocolHandler : IDisposable
    {
        public bool Transcode { get; set; }
        public bool useCrc { get; set; }
        public event Action<string>? Log;
        public int Timeout { get;set; }
        public bool Verbose { get; set; }
        public bool TryConnectChannel(string port);
        public Task SendPacketsAndReceiveAsync(
        ChannelWriter<Packet> receivedPacketsWriter,
        ChannelReader<Packet> packetsToSendReader,
        CancellationToken cancellationToken);
        public Packet SendPacketAndReceive(Packet packet);
        public Packet SendPacketAndReceive(SprdCommand type, IChecksum? checksum = null);
        public Packet SendPacketAndReceive(SprdCommand type,ReadOnlyMemory<byte> data,IChecksum? checksum = null);
        public Packet WriteBytesAndReceivePacket(byte[] Data);
        public byte[] WriteBytesAndReceiveBytes(byte[] Data);

    }
    public class SprdProtocolHandler : IDisposable,IProtocolHandler
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
        public bool Verbose { get; set; }
        public bool useCrc { get; set; }
        public event Action<string>? Log;


        byte[] rawPacketBuffer = new byte[0xffff];
        byte[] _tempBuffer = new byte[0x8000];

        private IEncoder _encoder;
        private bool disposed = false;
        private byte[] lastPacket;
        private SerialPort serialPort = new SerialPort();
        private Crc16Checksum crcChecksum = Crc16Checksum.Instance;
        private SprdChecksum sprdChecksum = SprdChecksum.Instance;
        private const byte HDLC_HEADER = 0x7E;
        private const byte HDLC_ESCAPE = 0x7D;
        private const ushort BSL_REP_LOG = 0x0000;

        public SprdProtocolHandler(IEncoder encoder)
        {
            Verbose = true;
            useCrc = false;
            _encoder = encoder;
            lastPacket = Array.Empty<byte>();
            Timeout = 1000;
        }
        public SprdProtocolHandler(string port, IEncoder encoder)
        {
            Verbose = true;
            useCrc = false;
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
                foreach (var port in FindAllPorts(identifier))
                {
                    return port;
                }
                Thread.Sleep(0);
            }
            throw new InvalidOperationException("Find no ports");
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
        private Packet ReceivePacket(byte[] packetBuffer, byte[] readBuffer)
        {
            Packet packet;
            while (true)
            {
                try
                {
                    packet = ReceivePacketInternal(packetBuffer, readBuffer);
                }
                catch (ExceptionDefinitions.ChecksumFailedException)
                {
                    try
                    {
                        packet = SendRetryRequest();
                    }
                    catch (ExceptionDefinitions.ChecksumFailedException)
                    {
                        useCrc = !useCrc;
                        packet = SendRetryRequest();
                    }
                }
                catch (ExceptionDefinitions.BadPacketException)
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

        private Packet ReceivePacketInternal(byte[] rawPacketBuffer, byte[] readBuffer)
        {
            int Length = ReceiveRawPacket(rawPacketBuffer, readBuffer);
            int writePosition = DecodePacket(rawPacketBuffer, Length);
            ValidateChecksum(rawPacketBuffer.AsMemory(0,writePosition));

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
                            throw new ExceptionDefinitions.BadPacketException("尾0x7e缺失");
                        }
                        break;
                    }
                }

                rawData[writePosition] = currentByte;
                writePosition++;

            }

            if (writePosition < 6)
            {
                throw new ExceptionDefinitions.BadPacketException("响应信息过短");
            }

            if (writePosition != expectedLength)
            {
                throw new ExceptionDefinitions.BadPacketException(
                    $"长度不一致: 实际 {writePosition} 字节, 预期 {expectedLength} 字节");
            }
            return writePosition;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReceiveRawPacket(byte[] rawPacketBuffer, byte[] readBuffer)
        {
            int read = ReadDataWithRetry(readBuffer);
            int expectedLength = BinaryPrimitives.ReadUInt16BigEndian(readBuffer.AsSpan(3, 2)) + 6;
            int packetLength = 0;
            packetLength += read;
            Buffer.BlockCopy(readBuffer, 0, rawPacketBuffer, 0, read);
            while (packetLength < expectedLength)
            {
                read = ReadDataWithRetry(readBuffer);
                Buffer.BlockCopy(readBuffer, 0, rawPacketBuffer, packetLength, read);
                packetLength += read;
            }
            return expectedLength;
        }
        private int TryReadRawData(byte[] buffer, int index, int count)
        {
            try { return serialPort.Read(buffer, index, count); }
            catch (TimeoutException) { return 0; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadDataWithRetry(byte[] buffer)
        {
            int retryCount = 0;
            int read;

            while ((read = TryReadRawData(buffer, 0, buffer.Length)) <= 0)
            {
                if (++retryCount > 5)
                {
                    throw new ExceptionDefinitions.ResponseTimeoutReachedException("响应超时");
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

            if (!useCrc)
            {
                ushort crcValue = crcChecksum.Compute(payload);
                ushort sumValue = sprdChecksum.Compute(payload);

                if (receivedChecksum != crcValue && receivedChecksum != sumValue)
                {
                    throw new ExceptionDefinitions.ChecksumFailedException(
                        $"校验和失败: 接收 0x{receivedChecksum:X4}, 预期 CRC 0x{crcValue:X4} 或 SUM 0x{sumValue:X4}");
                }
                useCrc = receivedChecksum == crcValue;
            }
            else
            {
                IChecksum checker = useCrc ? crcChecksum : sprdChecksum;
                ushort calculated = checker.Compute(payload);

                if (receivedChecksum != calculated)
                {
                    throw new ExceptionDefinitions.ChecksumFailedException(
                        $"校验和失败: 接收 0x{receivedChecksum:X4}, 预期 0x{calculated:X4}");
                }
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
                throw new ExceptionDefinitions.BadPacketException(
                    $"长度字段({dataLength})与实际包长度({packetLength})不匹配");
            }

            _recvPacket.Data = new byte[dataLength];
            Buffer.BlockCopy(packetBuffer, 4, _recvPacket.Data, 0, dataLength);
            _recvPacket.ChecksumStrategy = useCrc ? crcChecksum : sprdChecksum;

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
            return ReceivePacketInternal(rawPacketBuffer, _tempBuffer);
        }
        public async Task SendPacketsAndReceiveAsync(
            ChannelWriter<Packet> receivedPacketsWriter,
            ChannelReader<Packet> packetsToSendReader,
            CancellationToken cancellationToken)
        {
            var encodeAndSendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(20) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true,SingleWriter = true });
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
            await foreach (var packet in packetsToSendReader.ReadAllAsync(cancellationToken))
            {
                var encoded = _encoder.Encode(packet);
                lastPacket = encoded;
                await encodedDataWriter.WriteAsync(encoded, cancellationToken);
            }
            encodedDataWriter.Complete();
        }
        private async Task SendPacketsAndReceiveRawAsync(ChannelReader<byte[]> encodedDataReader,
            ChannelWriter<(byte[] data, int length)> rawDataWriter,
            CancellationToken cancellationToken)
        {
            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            byte[] readBuffer = new byte[0x8000];
            await foreach (var packetData in encodedDataReader.ReadAllAsync(cancellationToken))
            {
                serialPort.Write(packetData, 0, packetData.Length);
                byte[] packetBuffer = pool.Rent(0xffff);
                int expectedLength = ReceiveRawPacket(packetBuffer, readBuffer);
                await rawDataWriter.WriteAsync((packetBuffer, expectedLength), cancellationToken);
            }
            rawDataWriter.Complete();
        }
        private async Task DecodePacketsAsync(
            ChannelReader<(byte[] data, int length)> rawDataReader,
            ChannelWriter<Packet> writer,
            CancellationToken cancellationToken)
        {
            ArrayPool<byte> pool = ArrayPool<byte>.Shared;
            await foreach (var packetData in rawDataReader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    int writePosition = DecodePacket(packetData.data, packetData.length);
                    ValidateChecksum(packetData.data.AsMemory(0,writePosition));
                    await writer.WriteAsync(BuildPacket(writePosition, packetData.data), cancellationToken);
                }
                finally
                {
                    pool.Return(packetData.data, true);
                }
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
                checksum = useCrc ? crcChecksum : sprdChecksum;
            lastPacket = _encoder.Encode(type, data, checksum);
            serialPort.Write(lastPacket, 0, lastPacket.Length);
            if (Verbose) Log?.Invoke($"发送: {type}");
            return ReceivePacket(rawPacketBuffer, _tempBuffer);
        }
        public Packet WriteBytesAndReceivePacket(byte[] Data)
        {
            if (disposed) Thread.Sleep(new TimeSpan(1, 0, 0, 0));
            serialPort.Write(Data, 0, Data.Length);
            return ReceivePacket(rawPacketBuffer, _tempBuffer);
        }
        public byte[] WriteBytesAndReceiveBytes(byte[] Data)
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
