using System;
using System.IO.Ports;
using System.Threading.Tasks;

namespace ADC_Rec.Services
{
    public class SerialService : IDisposable
    {
        private SerialPort? _port = null;
        public event Action<byte[]>? DataReceived;
        public event Action<string>? LogMessage;

        public string[] GetPortNames() => SerialPort.GetPortNames();

        public bool IsConnected => _port?.IsOpen ?? false;

        public bool Connect(string portName, int baud = 115200)
        {
            try
            {
                _port = new SerialPort(portName, baud);
                _port.ReadTimeout = 500;
                _port.WriteTimeout = 500;
                _port.DataReceived += OnDataReceived;
                _port.Open();
                LogMessage?.Invoke($"Connected to {portName} @ {baud}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Connect error: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnDataReceived;
                    if (_port.IsOpen) _port.Close();
                    LogMessage?.Invoke("Disconnected");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Disconnect error: {ex.Message}");
            }
            finally
            {
                _port?.Dispose();
                _port = null;
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_port == null) return;
                int toRead = _port.BytesToRead;
                if (toRead > 0)
                {
                    byte[] buf = new byte[toRead];
                    int read = _port.Read(buf, 0, toRead);
                    if (read > 0)
                    {
                        DataReceived?.Invoke(buf);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Read error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}