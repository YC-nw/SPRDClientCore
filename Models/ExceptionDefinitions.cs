namespace SPRDClientCore.Models
{
    public static class ExceptionDefinitions
    {
        public class SprdException : Exception
        {
            public SprdException() : base() { }
            public SprdException(string message) : base(message) { }
        }

        public class UnexceptedResponseException : SprdException
        {
            public UnexceptedResponseException(SprdCommand sprdCommand) : base($"数据包响应异常类型 : {sprdCommand}") { }
        }

        public class ResponseTimeoutReachedException : SprdException
        {
            public ResponseTimeoutReachedException(string message) : base(message) { }
        }

        public class BadPacketException : SprdException
        {
            public BadPacketException(string message) : base(message) { }
        }

        public class ChecksumFailedException : SprdException
        {
            public ChecksumFailedException(string message) : base(message) { }
        }
    }
}
