using System.Management;

namespace SPRDClientCore.Utils
{

    public class ComPortMonitor
    {
        private ManagementEventWatcher _watcher;
        public Action? OnSerialPortDisconnected;

        public ComPortMonitor(string port, Action? onSerialPortDisconnected = null)
        {
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_DeviceChangeEvent " +
                "WHERE EventType = 3" // 3=设备移除
            );
            OnSerialPortDisconnected = onSerialPortDisconnected;
            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += (sender, e) => CheckPortStatus(port);
            _watcher.Start();
        }
        public void SetDisconnectedAction(Action action)
        {
            OnSerialPortDisconnected = action;
        }
        private void CheckPortStatus(string targetPort)
        {
            string[] availablePorts = GetAvailableCOMPorts().ToArray();
            bool portExists = Array.Exists(availablePorts, port => port == targetPort);

            if (!portExists)
            {
                OnSerialPortDisconnected?.Invoke();
            }
        }

        public void Stop()
        {
            _watcher?.Stop();
        }
        public static List<string> GetAvailableCOMPorts()
        {
            var comPorts = new List<string>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)%'"))
            {
                foreach (var obj in searcher.Get())
                {
                    string? name = obj["Name"] as string;
                    if (name != null)
                    {
                        int start = name.IndexOf("(COM") + 1;
                        int end = name.IndexOf(')', start);
                        if (start > 3 && end > start)
                        {
                            string str = name.Substring(start, end - start);
                            comPorts.Add(str);
                        }
                    }
                }
            }
            return comPorts;
        }

    }
}
