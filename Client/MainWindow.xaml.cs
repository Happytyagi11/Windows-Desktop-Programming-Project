using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Windows.Threading;


/*
*  FILE          : MainWindow.xaml.cs
*  PROJECT       : PROG2510 - Messaging - Client (WPF)
*  Team Members  : Nathanael Agnintedem Tsayem
*                  Tri Phan
*                  Happy
*  FIRST VERSION : 2025-11-25
*  DESCRIPTION   :
*    WPF client implementing the provided synchronous TCP messaging protocol:
*      - REGISTER|<username>
*      - WAITFORMESSAGE|<username> (long-poll loop)
*      - SEND|<from>|<to>|<message>
*      - UNREGISTER|<username>
*
*    Uses synchronous TCPClient/TcpListener methods, no 'var', and SET-style headers.
*/


namespace Client
{
    public partial class MainWindow : Window
    {
        // configuration fields
        private string _serverIp = "127.0.0.1";
        private int _serverPort = 9000;
        private string _username = "";

        // runtime fields
        private Thread? _waitThread;
        private bool _runningFlag = false;
        private const int WAIT_RECEIVE_TIMEOUT_MS = 60000; // 60s receive timeout so thread can check shutdown

        /*
        * FUNCTION     : MainWindow (constructor)
        * DESCRIPTION  : Initialize UI and start client registration + wait loop.
        * PARAMETERS   : None
        * RETURNS      : void
        */
        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            PromptForUsernameIfNeeded();
            bool registered = RegisterUsername();
            if (registered)
            {
                StartWaitLoop();
            }
        }

        /*
        * FUNCTION     : LoadSettings
        * DESCRIPTION  : Loads server IP and port from client.config (in exe folder).
        * PARAMETERS   : None
        * RETURNS      : void
        */
        private void LoadSettings()
        {
            string cfgPath = AppDomain.CurrentDomain.BaseDirectory + "client.config";
            try
            {
                if (!File.Exists(cfgPath))
                {
                    File.WriteAllText(cfgPath, "# client.config" + Environment.NewLine + "ip=127.0.0.1" + Environment.NewLine + "port=9000" + Environment.NewLine);
                }

                string[] lines = File.ReadAllLines(cfgPath);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("#"))
                    {
                        continue;
                    }

                    string[] parts = trimmed.Split(new char[] { '=' }, 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    string key = parts[0].Trim().ToLowerInvariant();
                    string value = parts[1].Trim();

                    if (key == "ip")
                    {
                        _serverIp = value;
                    }
                    else if (key == "port")
                    {
                        int portVal;
                        bool parsed = int.TryParse(value, out portVal);
                        if (parsed)
                        {
                            _serverPort = portVal;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error loading settings: " + ex.Message, "Error");
            }
        }

        /*
        * FUNCTION     : PromptForUsernameIfNeeded
        * DESCRIPTION  : If _username is empty, prompt the user via simple InputBox-style dialog.
        * PARAMETERS   : None
        * RETURNS      : void
        */
        private void PromptForUsernameIfNeeded()
        {
            if (_username != null && _username.Length > 0)
            {
                // already set
            }
            else
            {
                // simple prompt using InputDialog style via MessageBox (sequential)
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter your username for the chat:", "Username", Environment.MachineName);
                if (input != null && input.Trim().Length > 0)
                {
                    _username = input.Trim();
                }
                else
                {
                    // fallback to machine name
                    _username = Environment.MachineName;
                }
            }
        }

        /*
        * FUNCTION     : RegisterUsername
        * DESCRIPTION  : Sends REGISTER|<username> synchronously and reads response.
        * PARAMETERS   : None
        * RETURNS      : bool - true if registration succeeded
        */
        private bool RegisterUsername()
        {
            bool result = false;
            try
            {
                TcpClient client = new TcpClient();
                client.Connect(_serverIp, _serverPort);

                NetworkStream stream = client.GetStream();
                string request = "REGISTER|" + _username;
                byte[] requestBytes = Encoding.UTF8.GetBytes(request);
                stream.Write(requestBytes, 0, requestBytes.Length);

                // read response (blocking)
                byte[] buffer = new byte[4096];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    string response = Encoding.UTF8.GetString(buffer, 0, read);
                    AppendToConversation("[Server] " + response);
                    if (response.StartsWith("OK|", StringComparison.InvariantCultureIgnoreCase))
                    {
                        result = true;
                    }
                    else
                    {
                        // registration failed (user exists)
                        System.Windows.MessageBox.Show("Registration failed: " + response, "Register");
                        result = false;
                    }
                }

                stream.Close();
                client.Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Unable to register: " + ex.Message, "Error");
                result = false;
            }

            return result;
        }

        /*
        * FUNCTION     : StartWaitLoop
        * DESCRIPTION  : Starts a background thread that continuously performs WAITFORMESSAGE|<username>
        * PARAMETERS   : None
        * RETURNS      : void
        */
        private void StartWaitLoop()
        {
            _runningFlag = true;
            _waitThread = new Thread(new ThreadStart(WaitLoop));
            _waitThread.IsBackground = true;
            _waitThread.Start();
        }

        /*
        * FUNCTION     : WaitLoop
        * DESCRIPTION  : Long-poll loop: connect, send WAITFORMESSAGE|<username>, block waiting for event,
        *                process response, then immediately reconnect (until _runningFlag is false).
        * PARAMETERS   : None
        * RETURNS      : void
        */
        private void WaitLoop()
        {
            while (_runningFlag)
            {
                TcpClient? client = null;
                try
                {
                    client = new TcpClient();
                    // set receive timeout so that thread can check shutdown periodically
                    client.ReceiveTimeout = WAIT_RECEIVE_TIMEOUT_MS;
                    client.Connect(_serverIp, _serverPort);

                    NetworkStream stream = client.GetStream();
                    string req = "WAITFORMESSAGE|" + _username;
                    byte[] reqBytes = Encoding.UTF8.GetBytes(req);
                    stream.Write(reqBytes, 0, reqBytes.Length);

                    // Now block until server sends an event (or until ReceiveTimeout)
                    byte[] buffer = new byte[8192];
                    int readBytes = 0;
                    try
                    {
                        readBytes = stream.Read(buffer, 0, buffer.Length); // blocking
                    }
                    catch (IOException)
                    {
                        // timeout or network error — allow loop to continue and re-check _runningFlag
                        readBytes = 0;
                    }

                    if (readBytes > 0)
                    {
                        string payload = Encoding.UTF8.GetString(buffer, 0, readBytes);
                        ProcessServerEvent(payload);
                    }

                    // close stream & client for this session
                    try
                    {
                        stream.Close();
                    }
                    catch (Exception)
                    {
                        // ignore
                    }

                    client.Close();
                }
                catch (Exception ex)
                {
                    // network error — wait a short time and retry
                    AppendToConversation("[Error] WaitLoop: " + ex.Message);
                    try
                    {
                        if (client != null)
                        {
                            client.Close();
                        }
                    }
                    catch (Exception)
                    {
                        // ignore
                    }

                    // Sleep before retrying to avoid busy-loop
                    try
                    {
                        Thread.Sleep(1000);
                    }
                    catch (ThreadInterruptedException)
                    {
                        // allow thread to be interrupted on shutdown
                    }
                }
            }
        }

        /*
        * FUNCTION     : ProcessServerEvent
        * DESCRIPTION  : Parses server event strings and updates the UI accordingly.
        *               Supported events: MESSAGE|from|text, USERJOINED|user, USERLEFT|user, SYSTEM|text
        * PARAMETERS   : string payload - full server response
        * RETURNS      : void
        */
        private void ProcessServerEvent(string payload)
        {
            if (payload == null)
            {
                // do nothing
            }
            else
            {
                string[] parts = payload.Split(new char[] { '|' }, 3); // split into at most 3 parts
                if (parts.Length >= 1)
                {
                    string eventType = parts[0].Trim();
                    if (string.Equals(eventType, "MESSAGE", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (parts.Length >= 3)
                        {
                            string fromUser = parts[1];
                            string text = parts[2];
                            AppendToConversation("[MSG from " + fromUser + "] " + text);
                        }
                        else
                        {
                            AppendToConversation("[MSG] (malformed)");
                        }
                    }
                    else if (string.Equals(eventType, "USERJOINED", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (parts.Length >= 2)
                        {
                            string user = parts[1];
                            AppendToConversation("[User Joined] " + user);
                        }
                        else
                        {
                            AppendToConversation("[User Joined] (unknown)");
                        }
                    }
                    else if (string.Equals(eventType, "USERLEFT", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (parts.Length >= 2)
                        {
                            string user = parts[1];
                            AppendToConversation("[User Left] " + user);
                        }
                        else
                        {
                            AppendToConversation("[User Left] (unknown)");
                        }
                    }
                    else if (string.Equals(eventType, "SYSTEM", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (parts.Length >= 2)
                        {
                            string text = parts[1];
                            AppendToConversation("[System] " + text);
                        }
                        else
                        {
                            AppendToConversation("[System] (no message)");
                        }
                    }
                    else if (string.Equals(eventType, "NOEVENT", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // no event — nothing to display (used only if server implements timeout)
                    }
                    else
                    {
                        // Unknown event — display raw
                        AppendToConversation("[Event] " + payload);
                    }
                }
            }
        }

        /*
* FUNCTION     : SendButton_Click
* DESCRIPTION  : Validates fields and sends message only when both fields are filled.
* PARAMETERS   : object sender, RoutedEventArgs e
* RETURNS      : void
*/
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            bool valid = true;

            string recipient = RecipientBox.Text.Trim();
            string message = MessageBox.Text.Trim();

            // Validate recipient
            if (recipient.Length == 0)
            {
                RecipientWarning.Visibility = Visibility.Visible;
                valid = false;
            }
            else
            {
                RecipientWarning.Visibility = Visibility.Collapsed;
            }

            // Validate message
            if (message.Length == 0)
            {
                MessageWarning.Visibility = Visibility.Visible;
                valid = false;
            }
            else
            {
                MessageWarning.Visibility = Visibility.Collapsed;
            }

            // Stop if invalid
            if (!valid)
            {
                return;
            }

            // If valid → proceed with SEND request
            try
            {
                TcpClient client = new TcpClient();
                client.Connect(_serverIp, _serverPort);

                NetworkStream stream = client.GetStream();
                string sendReq = "SEND|" + _username + "|" + recipient + "|" + message;
                byte[] reqBytes = Encoding.UTF8.GetBytes(sendReq);
                stream.Write(reqBytes, 0, reqBytes.Length);

                // Read ACK
                byte[] buffer = new byte[4096];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    string response = Encoding.UTF8.GetString(buffer, 0, read);
                    AppendToConversation("[Sent to " + recipient + "] " + message);
                    AppendToConversation("[Server] " + response);
                    MessageBox.Text = "";
                }

                stream.Close();
                client.Close();
            }
            catch (Exception ex)
            {
                AppendToConversation("[Send Error] " + ex.Message);
            }

            return;
        }


        /*
        * FUNCTION     : UnregisterUsername
        * DESCRIPTION  : Sends UNREGISTER|<username> and reads response. Used on shutdown.
        * PARAMETERS   : None
        * RETURNS      : bool - true if unregistered ok
        */
        private bool UnregisterUsername()
        {
            bool result = false;
            try
            {
                TcpClient client = new TcpClient();
                client.Connect(_serverIp, _serverPort);

                NetworkStream stream = client.GetStream();
                string req = "UNREGISTER|" + _username;
                byte[] bytes = Encoding.UTF8.GetBytes(req);
                stream.Write(bytes, 0, bytes.Length);

                byte[] buffer = new byte[4096];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    string response = Encoding.UTF8.GetString(buffer, 0, read);
                    AppendToConversation("[Server] " + response);
                    if (response.StartsWith("OK|", StringComparison.InvariantCultureIgnoreCase))
                    {
                        result = true;
                    }
                }

                stream.Close();
                client.Close();
            }
            catch (Exception ex)
            {
                AppendToConversation("[Unregister Error] " + ex.Message);
                result = false;
            }

            return result;
        }

        /*
        * FUNCTION     : AppendToConversation
        * DESCRIPTION  : Thread-safe append to the conversation TextBox.
        * PARAMETERS   : string text - text to append
        * RETURNS      : void
        */
        private void AppendToConversation(string text)
        {
            if (text == null)
            {
                // nothing
            }
            else
            {
                Dispatcher.Invoke(new Action(delegate
                {
                    ConversationBox.Text = ConversationBox.Text + Environment.NewLine + text;
                    ConversationBox.ScrollToEnd();
                }));
            }
        }

        /*
        * FUNCTION     : About_Click
        * DESCRIPTION  : Shows an About dialog.
        * PARAMETERS   : object sender, RoutedEventArgs e
        * RETURNS      : void
        */
        private void About_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("LAN Messaging Client\nPROG2510 Project", "About");
        }

        /*
        * FUNCTION     : Settings_Click
        * DESCRIPTION  : Opens the client.config file in Notepad for editing.
        * PARAMETERS   : object sender, RoutedEventArgs e
        * RETURNS      : void
        */
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            string cfgPath = AppDomain.CurrentDomain.BaseDirectory + "client.config";
            try
            {
                if (!File.Exists(cfgPath))
                {
                    File.WriteAllText(cfgPath, "# client.config" + Environment.NewLine + "ip=127.0.0.1" + Environment.NewLine + "port=9000" + Environment.NewLine);
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", cfgPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Unable to open settings: " + ex.Message);
            }
        }

        /*
        * FUNCTION     : Window_Closing
        * DESCRIPTION  : Called when window closes; stops wait thread and unregisters user.
        * PARAMETERS   : object sender, System.ComponentModel.CancelEventArgs e
        * RETURNS      : void
        */
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _runningFlag = false;

            // Give the wait thread a chance to exit gracefully
            try
            {
                if (_waitThread != null)
                {
                    _waitThread.Join(2000); // wait up to 2 seconds
                }
            }
            catch (Exception)
            {
                // ignore
            }

            // Attempt to unregister (best-effort)
            bool unregistered = UnregisterUsername();

            // final log to conversation
            if (unregistered)
            {
                AppendToConversation("[Unregistered]");
            }
            else
            {
                AppendToConversation("[Unregister attempt failed]");
            }

            return;
        }

        private void RecipientBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (RecipientBox.Text.Trim().Length > 0)
            {
                RecipientWarning.Visibility = Visibility.Collapsed;
            }
        }

        private void MessageBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (MessageBox.Text.Trim().Length > 0)
            {
                MessageWarning.Visibility = Visibility.Collapsed;
            }
        }

    }
}
