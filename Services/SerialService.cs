using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ADC_Rec.Services
{
    public class SerialService : IDisposable
    {
        private SerialPort? _port;
        private CancellationTokenSource? _cts;
        public event Action<byte[]>? DataReceived;
        public event Action<string>? LogMessage;

        public bool Connect(string portName, int baud = 115200)
        {
            try
            {
                _port = new SerialPort(portName, baud);
                _port.Open();

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));

                LogMessage?.Invoke($"Connected to {portName} @ {baud}");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Connect error: {ex.Message}");
                return false;
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var stream = _port!.BaseStream;
            byte[] buffer = new byte[4096];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read > 0)
                    {
                        byte[] data = new byte[read];
                        Buffer.BlockCopy(buffer, 0, data, 0, read);
                        DataReceived?.Invoke(data);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Read error: {ex.Message}");
                    break;
                }
            }
        }

        public void Disconnect()
        {
            try
            {
                _cts?.Cancel();
                _port?.Close();
                LogMessage?.Invoke("Disconnected");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Disconnect error: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _port?.Dispose();
                _cts = null;
                _port = null;
            }
        }

        public void Dispose() => Disconnect();

        public string[] GetPortNames()
        {
            return SerialPort.GetPortNames();
        }

        public bool IsConnected => _port?.IsOpen ?? false;
    }
}