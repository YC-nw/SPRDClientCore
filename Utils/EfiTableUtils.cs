using System.Text;
using System.Xml;
using SPRDClientCore.Models;

namespace SPRDClientCore.Utils
{
    public class EfiTableUtils
    {
        public (Partition[]? partitions, bool? isEmmc) GetPartitions(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                // 搜索EFI分区头
                const int sectorSize = 512;
                int sectorIndex = 0;
                bool found = false;

                while (sectorIndex < 32)
                {
                    fs.Position = sectorIndex * sectorSize;
                    byte[] signatureBytes = reader.ReadBytes(8);
                    if (Encoding.ASCII.GetString(signatureBytes) == "EFI PART")
                    {
                        found = true;
                        break;
                    }
                    sectorIndex++;
                }

                if (!found)
                {
                    return (null, false);
                }

                // 读取分区表头
                fs.Position = sectorIndex * sectorSize;
                EfiHeader header = ReadEfiHeader(reader);

                // 读取分区条目
                fs.Position = header.PartitionEntryLba * sectorSize;
                List<EfiEntry> entries = ReadPartitionEntries(reader, header);
                List<Partition> partList = new List<Partition>();
                // 生成XML文件
                foreach (var entry in entries)
                {
                    if (entry.StartingLba == 0 && entry.EndingLba == 0)
                        continue;

                    string name = Encoding.Unicode.GetString(entry.PartitionName)
                        .TrimEnd('\0')
                        .Replace("\0", "");

                    long sizeSectors = entry.EndingLba - entry.StartingLba + 1;
                    double sizeMB = sizeSectors * sectorSize / (1024.0 * 1024.0);
                    partList.Add(new Partition { Name = name, Size = (ulong)sizeMB });
                }
                return (partList.ToArray(), sectorIndex == 1);
            }
        }
        public (List<Partition> partitions, bool? isEmmc) GetPartitions(MemoryStream ms)
        {
            using (BinaryReader reader = new BinaryReader(ms))
            {
                List<Partition> partList = new List<Partition>();

                // 搜索EFI分区头
                const int sectorSize = 512;
                int sectorIndex = 0;
                bool found = false;

                while (sectorIndex < 32)
                {
                    ms.Position = sectorIndex * sectorSize;
                    byte[] signatureBytes = reader.ReadBytes(8);
                    if (Encoding.ASCII.GetString(signatureBytes) == "EFI PART")
                    {
                        found = true;
                        break;
                    }
                    sectorIndex++;
                }

                if (!found)
                {
                    return (partList, false);
                }

                // 读取分区表头
                ms.Position = sectorIndex * sectorSize;
                EfiHeader header = ReadEfiHeader(reader);

                // 读取分区条目
                ms.Position = header.PartitionEntryLba * sectorSize;
                List<EfiEntry> entries = ReadPartitionEntries(reader, header);
                // 生成XML文件
                foreach (var entry in entries)
                {
                    if (entry.StartingLba == 0 && entry.EndingLba == 0)
                        continue;

                    string name = Encoding.Unicode.GetString(entry.PartitionName)
                        .TrimEnd('\0')
                        .Replace("\0", "");

                    long sizeSectors = entry.EndingLba - entry.StartingLba + 1;
                    double sizeMB = sizeSectors * sectorSize;
                    partList.Add(new Partition { Name = name, Size = (ulong)sizeMB, IndicesToMB = 20 });
                }
                return (partList, sectorIndex == 1);
            }
        }
        static EfiHeader ReadEfiHeader(BinaryReader reader)
        {
            return new EfiHeader
            {
                Signature = reader.ReadBytes(8),
                Revision = reader.ReadInt32(),
                HeaderSize = reader.ReadInt32(),
                HeaderCrc32 = reader.ReadInt32(),
                Reserved = reader.ReadInt32(),
                CurrentLba = reader.ReadInt64(),
                BackupLba = reader.ReadInt64(),
                FirstUsableLba = reader.ReadInt64(),
                LastUsableLba = reader.ReadInt64(),
                DiskGuid = reader.ReadBytes(16),
                PartitionEntryLba = reader.ReadInt64(),
                NumberOfPartitionEntries = reader.ReadInt32(),
                SizeOfPartitionEntry = reader.ReadInt32(),
                PartitionEntryArrayCrc32 = reader.ReadInt32()
            };
        }

        static List<EfiEntry> ReadPartitionEntries(BinaryReader reader, EfiHeader header)
        {
            List<EfiEntry> entries = new List<EfiEntry>();
            int entrySize = Math.Max(header.SizeOfPartitionEntry, 128);

            for (int i = 0; i < header.NumberOfPartitionEntries; i++)
            {
                long pos = reader.BaseStream.Position;
                EfiEntry entry = new EfiEntry
                {
                    PartitionTypeGuid = reader.ReadBytes(16),
                    UniquePartitionGuid = reader.ReadBytes(16),
                    StartingLba = reader.ReadInt64(),
                    EndingLba = reader.ReadInt64(),
                    Attributes = reader.ReadInt64(),
                    PartitionName = reader.ReadBytes(72)
                };

                // 跳过可能存在的填充字节
                if (entrySize > 128)
                    reader.ReadBytes(entrySize - 128);

                entries.Add(entry);
            }
            return entries;
        }

        static void GeneratePartitionXml(List<EfiEntry> entries, int sectorSize = 512)
        {
            XmlWriterSettings settings = new XmlWriterSettings { Indent = true };
            using (XmlWriter writer = XmlWriter.Create("partition.xml", settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("Partitions");

                foreach (var entry in entries)
                {
                    if (entry.StartingLba == 0 && entry.EndingLba == 0)
                        continue;

                    string name = Encoding.Unicode.GetString(entry.PartitionName)
                        .TrimEnd('\0')
                        .Replace("\0", "");

                    long sizeSectors = entry.EndingLba - entry.StartingLba + 1;
                    double sizeMB = sizeSectors * sectorSize / (1024.0 * 1024.0);

                    writer.WriteStartElement("Partition");
                    writer.WriteAttributeString("id", name);
                    writer.WriteAttributeString("size",
                        name == "userdata" ? "0xFFFFFFFF" : ((int)sizeMB).ToString());
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }
        static void GetPartitionList(List<EfiEntry> entries, int sectorSize)
        {
            XmlWriterSettings settings = new XmlWriterSettings { Indent = true };
            using (XmlWriter writer = XmlWriter.Create("partition.xml", settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("Partitions");

                foreach (var entry in entries)
                {
                    if (entry.StartingLba == 0 && entry.EndingLba == 0)
                        continue;

                    string name = Encoding.Unicode.GetString(entry.PartitionName)
                        .TrimEnd('\0')
                        .Replace("\0", "");

                    long sizeSectors = entry.EndingLba - entry.StartingLba + 1;
                    double sizeMB = sizeSectors * sectorSize / (1024.0 * 1024.0);

                    writer.WriteStartElement("Partition");
                    writer.WriteAttributeString("id", name);
                    writer.WriteAttributeString("size",
                        name == "userdata" ? "0xFFFFFFFF" : ((int)sizeMB).ToString());
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        struct EfiHeader
        {
            public byte[]? Signature;
            public int Revision;
            public int HeaderSize;
            public int HeaderCrc32;
            public int Reserved;
            public long CurrentLba;
            public long BackupLba;
            public long FirstUsableLba;
            public long LastUsableLba;
            public byte[]? DiskGuid;
            public long PartitionEntryLba;
            public int NumberOfPartitionEntries;
            public int SizeOfPartitionEntry;
            public int PartitionEntryArrayCrc32;
        }
        struct EfiEntry
        {
            public byte[]? PartitionTypeGuid;
            public byte[]? UniquePartitionGuid;
            public long StartingLba;
            public long EndingLba;
            public long Attributes;
            public byte[] PartitionName;
        }
    }
}
