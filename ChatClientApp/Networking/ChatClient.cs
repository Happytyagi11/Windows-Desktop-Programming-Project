using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Client.Networking
{
    public class ChatClient
    {
        private TcpClient tcpClient;
        private NetworkStream networkStream;
        private StreamReader reader;
        private StreamWriter writer;
        private Thread listenThread;
        private bool isConnected;
        private string currentUserName;

        // Events for UI layer
        public event Action<string, string, string> MessageReceived;
        public event Action<string> ErrorOccurred;
        public event Action Disconnected;

        // Connect to server
        public bool Connect(string serverIp, int serverPort, string userName)
        {
            bool success = false;

            try
            {
                tcpClient = new TcpClient();
                tcpClient.Connect(serverIp, serverPort);

                networkStream = tcpClient.GetStream();
                reader = new StreamReader(networkStream);
                writer = new StreamWriter(networkStream);
                writer.AutoFlush = true;

                isConnected = true;
                currentUserName = userName;

                listenThread = new Thread(ListenForMessages);
                listenThread.IsBackground = true;
                listenThread.Start();

                success = true;
            }
            catch (Exception ex)
            {
                isConnected = false;
                RaiseError("Failed to connect: " + ex.Message);
            }

            return success;
        }

        // Send a chat message
        public bool SendChatMessage(string receiverUserName, string messageBody)
        {
            bool success = false;

            try
            {
                if (!isConnected)
                {
                    RaiseError("Not connected to server.");
                }
                else
                {
                    string line = "MSG|" + currentUserName + "|" + receiverUserName + "|" + messageBody;
                    writer.WriteLine(line);
                    success = true;
                }
            }
            catch (Exception ex)
            {
                RaiseError("Failed to send message: " + ex.Message);
                isConnected = false;
                RaiseDisconnected();
            }

            return success;
        }

        // Listen for messages from server
        private void ListenForMessages()
        {
            try
            {
                while (isConnected)
                {
                    string? line = reader.ReadLine();

                    if (line == null)
                    {
                        isConnected = false;
                    }
                    else
                    {
                        string[] parts = line.Split('|');

                        if (parts.Length >= 4)
                        {
                            string type = parts[0];
                            string sender = parts[1];
                            string receiver = parts[2];
                            string body = parts[3];

                            if (type == "MSG")
                            {
                                RaiseMessageReceived(sender, receiver, body);
                            }
                            else if (type == "ACK")
                            {
                                // Optional handling
                            }
                            else
                            {
                                RaiseError("Unknown message type: " + type);
                            }
                        }
                        else
                        {
                            RaiseError("Invalid message format: " + line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseError("Error while listening: " + ex.Message);
            }

            isConnected = false;
            RaiseDisconnected();
        }

        // Disconnect cleanly
        public void Disconnect()
        {
            try
            {
                isConnected = false;

                if (listenThread != null && listenThread.IsAlive)
                {
                    listenThread.Join(500);
                }

                if (reader != null)
                {
                    reader.Close();
                }

                if (writer != null)
                {
                    writer.Close();
                }

                if (networkStream != null)
                {
                    networkStream.Close();
                }

                if (tcpClient != null)
                {
                    tcpClient.Close();
                }
            }
            catch (Exception ex)
            {
                RaiseError("Error during disconnect: " + ex.Message);
            }

            RaiseDisconnected();
        }

        // Event helper methods
        private void RaiseError(string message)
        {
            if (ErrorOccurred != null)
            {
                ErrorOccurred(message);
            }
        }

        private void RaiseMessageReceived(string sender, string receiver, string message)
        {
            if (MessageReceived != null)
            {
                MessageReceived(sender, receiver, message);
            }
        }

        private void RaiseDisconnected()
        {
            if (Disconnected != null)
            {
                Disconnected();
            }
        }
    }
}
