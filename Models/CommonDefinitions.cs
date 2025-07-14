using System.Runtime.InteropServices;
using SPRDClientCore.Protocol.CheckSums;

namespace SPRDClientCore.Models
{

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DaInfo
    {
        public uint dwVersion;
        public uint bDisableHDLC; // 0: Enable HDLC, 1: Disable HDLC
        public byte bIsOldMemory;
        public byte bSupportRawData;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] bReserve; // 2字节预留
        public uint dwFlushSize; // 单位 KB
        public uint dwStorageType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 59)]
        public uint[] dwReserve; // 59个预留uint
    }
    public enum GetPartitionsMethod : ushort
    {
        ConvertExtTable = 0,
        SendReadPartitionPacket = 1,
        TraverseCommonPartitions = 2,
    }
    public enum Stages : ushort
    {
        Brom = 0,
        Fdl1 = 1,
        Fdl2 = 2,
        Sprd3 = 3,
        Sprd4 = 4
    }
    public enum CustomModesToReset
    {
        FactoryReset,
        Recovery,
        Fastboot,
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    public struct Partition
    {
        public string Name;
        public ulong Size;
        public int IndicesToMB;
        public static string[] CommonPartitions { get; } = {"splloader","prodnv", "miscdata", "recovery", "misc", "trustos", "trustos_bak",
       "sml", "sml_bak", "uboot", "uboot_bak", "logo","logo_1" ,"logo_2","logo_3","logo_4","logo_5","logo_6","fbootlogo",
     "l_fixnv1", "l_fixnv2", "l_runtimenv1", "l_runtimenv2",
     "gpsgl", "gpsbd", "wcnmodem", "persist", "l_modem",
     "l_deltanv", "l_gdsp", "l_ldsp", "pm_sys", "boot",
     "system",  "cache","vendor", "uboot_log", "userdata","dtb","socko","vbmeta","vbmeta_bak","vbmeta_system",
     "trustos_a", "trustos_b", "sml_a", "sml_b", "teecfg","teecfg_a", "teecfg_b",
     "uboot_a", "uboot_b", "gnssmodem_a", "gnssmodem_b", "wcnmodem_a",
     "wcnmodem_b", "l_modem_a", "l_modem_b", "l_deltanv_a", "l_deltanv_b",
     "l_gdsp_a", "l_gdsp_b", "l_ldsp_a", "l_ldsp_b", "l_agdsp_a", "l_agdsp_b",
    "l_cdsp_a", "l_cdsp_b", "pm_sys_a", "pm_sys_b", "boot_a", "boot_b",
    "vendor_boot_a", "vendor_boot_b", "dtb_a", "dtb_b", "dtbo_a", "dtbo_b",
    "super", "socko_a", "socko_b", "odmko_a", "odmko_b", "vbmeta_a", "vbmeta_b",
    "metadata", "sysdumpdb", "vbmeta_system_a", "vbmeta_system_b",
    "vbmeta_vendor_a", "vbmeta_vendor_b", "vbmeta_system_ext_a",
    "vbmeta_system_ext_b", "vbmeta_product_a","ubipac", "vbmeta_product_b","user_partition"
};
        public override string ToString()
        {
            decimal sizeValue = (decimal)Size / (1UL << IndicesToMB);
            return $"name : {Name} , size : {Math.Round(sizeValue, 0)}";
        }
    }
    public enum ModeOfChangingDiagnostic : ushort
    {
        CustomOneTimeMode = 0, //发一次包，老设备不支持
        CommonMode = 1 //to cali发一次，to dl_diag发两次
    }
    public enum ModeToChange : ushort
    {
        CaliDiagnostic = 1,
        DlDiagnostic = 2
    }
    public enum SprdCommand : ushort
    {
        /* Link Control */
        BSL_CMD_CONNECT = 0x00,


        /* Data Download */
        BSL_CMD_START_DATA = 0x01, /* The start flag of the data downloading */
        BSL_CMD_MIDST_DATA = 0x02, /* The midst flag of the data downloading */
        BSL_CMD_END_DATA = 0x03, /* The end flag of the data downloading */
        BSL_CMD_EXEC_DATA = 0x04, /* Execute from a certain address */

        BSL_CMD_NORMAL_RESET = 0x05, /* Reset to normal mode */
        BSL_CMD_READ_FLASH = 0x06, /* Read flash content */
        BSL_CMD_READ_CHIP_TYPE = 0x07, /* Read chip type */
        BSL_CMD_READ_NVITEM = 0x08, /* Lookup a nvitem in specified area */
        BSL_CMD_CHANGE_BAUD = 0x09, /* Change baudrate */
        BSL_CMD_ERASE_FLASH = 0x0A, /* Erase an area of flash */
        BSL_CMD_REPARTITION = 0x0B, /* Repartition nand flash */
        BSL_CMD_READ_FLASH_TYPE = 0x0C, /* Read flash type */
        BSL_CMD_READ_FLASH_INFO = 0x0D, /* Read flash infomation */
        BSL_CMD_READ_SECTOR_SIZE = 0x0F, /* Read Nor flash sector size */
        BSL_CMD_READ_START = 0x10, /* Read flash start */
        BSL_CMD_READ_MIDST = 0x11, /* Read flash midst */
        BSL_CMD_READ_END = 0x12, /* Read flash end */

        BSL_CMD_KEEP_CHARGE = 0x13, /* Keep charge */
        BSL_CMD_EXTTABLE = 0x14, /* Set ExtTable */
        BSL_CMD_READ_FLASH_UID = 0x15, /* Read flash UID */
        BSL_CMD_READ_SOFTSIM_EID = 0x16, /* Read softSIM EID */
        BSL_CMD_POWER_OFF = 0x17, /* Power Off */
        BSL_CMD_CHECK_ROOT = 0x19, /* Check Root */
        BSL_CMD_READ_CHIP_UID = 0x1A, /* Read Chip UID */
        BSL_CMD_ENABLE_WRITE_FLASH = 0x1B, /* Enable flash */
        BSL_CMD_ENABLE_SECUREBOOT = 0x1C, /* Enable secure boot */
        BSL_CMD_IDENTIFY_START = 0x1D, /* Identify start */
        BSL_CMD_IDENTIFY_END = 0x1E, /* Identify end */
        BSL_CMD_READ_CU_REF = 0x1F, /* Read CU ref */
        BSL_CMD_READ_REFINFO = 0x20, /* Read Ref Info */
        BSL_CMD_DISABLE_TRANSCODE = 0x21, /* Use the non-escape function */
        BSL_CMD_WRITE_DATETIME = 0x22, /* Write pac file build time to miscdata */
        BSL_CMD_CUST_DUMMY = 0x23, /* Customized Dummy */
        BSL_CMD_READ_RF_TRANSCEIVER_TYPE = 0x24, /* Read RF transceiver type */
        BSL_CMD_SET_DEBUGINFO = 0x25,
        BSL_CMD_DDR_CHECK = 0x26,
        BSL_CMD_SELF_REFRESH = 0x27,
        BSL_CMD_WRITE_RAW_DATA_ENABLE = 0x28, /* Init for 0x31 and 0x33 */
        BSL_CMD_READ_NAND_BLOCK_INFO = 0x29,
        BSL_CMD_SET_FIRST_MODE = 0x2A,
        BSL_CMD_READ_PARTITION = 0x2D, /* Partition list */
        BSL_CMD_DLOAD_RAW_START = 0x31, /* Raw packet */
        BSL_CMD_WRITE_FLUSH_DATA = 0x32,
        BSL_CMD_DLOAD_RAW_START2 = 0x33, /* Whole raw file */
        BSL_CMD_READ_LOG = 0x35,

        BSL_CMD_CHECK_BAUD = 0x7E, /* CheckBaud command, for internal use */
        BSL_CMD_END_PROCESS = 0x7F, /* End flash process */

        /* response from the phone */
        BSL_REP_ACK = 0x80, /* The operation acknowledge */
        BSL_REP_VER = 0x81,
        BSL_REP_INVALID_CMD = 0x82,
        BSL_REP_UNKNOW_CMD = 0x83,
        BSL_REP_OPERATION_FAILED = 0x84,

        /* Link Control */
        BSL_REP_NOT_SUPPORT_BAUDRATE = 0x85,

        /* Data Download */
        BSL_REP_DOWN_NOT_START = 0x86,
        BSL_REP_DOWN_MULTI_START = 0x87,
        BSL_REP_DOWN_EARLY_END = 0x88,
        BSL_REP_DOWN_DEST_ERROR = 0x89,
        BSL_REP_DOWN_SIZE_ERROR = 0x8A,
        BSL_REP_VERIFY_ERROR = 0x8B,
        BSL_REP_NOT_VERIFY = 0x8C,

        /* Phone Internal Error */
        BSL_PHONE_NOT_ENOUGH_MEMORY = 0x8D,
        BSL_PHONE_WAIT_INPUT_TIMEOUT = 0x8E,

        /* Phone Internal return value */
        BSL_PHONE_SUCCEED = 0x8F,
        BSL_PHONE_VALID_BAUDRATE = 0x90,
        BSL_PHONE_REPEAT_CONTINUE = 0x91,
        BSL_PHONE_REPEAT_BREAK = 0x92,

        /* End of the Command can be transmited by phone */
        BSL_REP_READ_FLASH = 0x93,
        BSL_REP_READ_CHIP_TYPE = 0x94,
        BSL_REP_READ_NVITEM = 0x95,

        BSL_REP_INCOMPATIBLE_PARTITION = 0x96,
        BSL_REP_SIGN_VERIFY_ERROR = 0xA6,
        BSL_REP_READ_CHIP_UID = 0xAB,
        BSL_REP_READ_PARTITION = 0xBA,
        BSL_REP_READ_LOG = 0xBB,
        BSL_REP_UNSUPPORTED_COMMAND = 0xFE,
        BSL_REP_LOG = 0xFF,
    };
}
