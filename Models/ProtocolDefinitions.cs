using System.Threading.Channels;

namespace SPRDClientCore.Models
{
    public interface IEncoder
    {
        public bool UseTranscode { get; set; }
        public byte[] Encode(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum checksum);
        public byte[] Encode(Packet packet);
    }
    public interface IProtocolHandler : IDisposable
    {
        public string PortName { get; }
        public bool Transcode { get; set; }
        public bool UseCrc { get; set; }
        public event Action<string>? Log;
        public int Timeout { get; set; }
        public bool Verbose { get; set; }
        public bool IsPortOpen { get; }
        public bool TryConnectChannel(string port);
        public Task SendPacketsAndReceiveAsync(
        ChannelWriter<Packet> receivedPacketsWriter,
        ChannelReader<Packet> packetsToSendReader,
        CancellationToken cancellationToken);
        public Packet SendPacketAndReceive(Packet packet);
        public Packet SendPacketAndReceive(SprdCommand type, IChecksum? checksum = null);
        public Packet SendPacketAndReceive(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum? checksum = null);
        public Packet SendBytesAndReceivePacket(byte[] Data);
        public byte[] SendBytesAndReceiveBytes(byte[] Data);

    }

    public struct Packet
    {
        public SprdCommand Type { get; set; }
        public byte[] Data { get; set; }
        public IChecksum ChecksumStrategy { get; set; }

        public Packet(SprdCommand type, byte[]? data, IChecksum checksumStrategy)
        {
            Type = type;
            Data = data ?? Array.Empty<byte>();
            ChecksumStrategy = checksumStrategy;
        }

        public override readonly string ToString()
        {
            return $"Type : 0x{Type.ToString("X")} , Data : {BitConverter.ToString(Data)}";
        }
    }
    public interface IChecksum
    {
        ushort Compute(params ReadOnlyMemory<byte>[] datas);
    }

}
