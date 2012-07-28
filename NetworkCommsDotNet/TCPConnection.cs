﻿//  Copyright 2011 Marc Fletcher, Matthew Dean
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Threading.Tasks;

namespace NetworkCommsDotNet
{
    /// <summary>
    /// A TCPConnection represents each established tcp connection between two peers.
    /// </summary>
    public class TCPConnection : Connection
    {
        /// <summary>
        /// The TcpClient corresponding to this connection.
        /// </summary>
        TcpClient tcpClient;

        /// <summary>
        /// The networkstream associated with the tcpClient.
        /// </summary>
        NetworkStream tcpClientNetworkStream;

        /// <summary>
        /// Create a connection with the provided connectionInfo. If there is an existing connection that is returned instead.
        /// </summary>
        /// <param name="connectionInfo"></param>
        /// <param name="establishIfRequired"></param>
        /// <returns></returns>
        public static TCPConnection CreateConnection(ConnectionInfo connectionInfo, bool establishIfRequired = true)
        {
            return CreateConnection(connectionInfo, null, establishIfRequired);
        }

        /// <summary>
        /// Internal create which has the possibility of an existing TcpClient
        /// </summary>
        /// <param name="connectionInfo"></param>
        /// <param name="tcpClient"></param>
        /// <param name="establishIfRequired"></param>
        /// <returns></returns>
        internal static TCPConnection CreateConnection(ConnectionInfo connectionInfo, TcpClient tcpClient, bool establishIfRequired = true)
        {
            bool newConnection = false;
            TCPConnection connection;

            lock (NetworkComms.globalDictAndDelegateLocker)
            {
                //Check to see if a conneciton already exists, if it does return that connection, if not return a new one
                if (NetworkComms.ConnectionExists(connectionInfo.RemoteEndPoint, connectionInfo.ConnectionType))
                    connection = (TCPConnection)NetworkComms.RetrieveConnection(connectionInfo.RemoteEndPoint, connectionInfo.ConnectionType);
                else
                {
                    //We add a reference to networkComms for this connection within the constructor
                    connection = new TCPConnection(connectionInfo, tcpClient);
                    newConnection = true;
                }
            }

            if (establishIfRequired)
            {
                if (newConnection) connection.EstablishConnection();
                else  connection.WaitForConnectionEstablish(NetworkComms.ConnectionEstablishTimeoutMS);
            }

            return connection;
        }

        /// <summary>
        /// TCP connection constructor
        /// </summary>
        protected TCPConnection(ConnectionInfo connectionInfo, TcpClient tcpClient)
            : base(connectionInfo)
        {
            //We don't guarantee that the tcpClient has been created yet
            if (tcpClient != null) this.tcpClient = tcpClient;
        }

        public static void ListenForConnectionWorker()
        {
            //Do we listen for new connections here??
        }

        /// <summary>
        /// Establish a connection with the provided TcpClient
        /// </summary>
        /// <param name="sourceClient"></param>
        protected override void EstablishConnectionInternal()
        {
            if (tcpClient == null) ConnectTCPClient();

            //We are going to be using the networkStream quite a bit so we pull out a reference once here
            tcpClientNetworkStream = tcpClient.GetStream();

            //When we tell the socket/client to close we want it to do so immediately
            //this.tcpClient.LingerState = new LingerOption(false, 0);

            //We need to set the keep alive option otherwise the connection will just die at some random time should we not be using it
            //NOTE: This did not seem to work reliably so was replaced with the keepAlive packet feature
            //this.tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            tcpClient.ReceiveBufferSize = NetworkComms.receiveBufferSizeBytes;
            tcpClient.SendBufferSize = NetworkComms.sendBufferSizeBytes;

            //This disables the 'nagle alogrithm'
            //http://msdn.microsoft.com/en-us/library/system.net.sockets.socket.nodelay.aspx
            //Basically we may want to send lots of small packets (<200 bytes) and sometimes those are time critical (e.g. when establishing a connection)
            //If we leave this enabled small packets may never be sent until a suitable send buffer length threshold is passed. i.e. BAD
            tcpClient.NoDelay = true;
            tcpClient.Client.NoDelay = true;

            //Start listening for incoming data
            StartIncomingDataListen();

            //If we are server side and we have just received an incoming connection we need to return a conneciton id
            //This id will be used in all future connections from this machine
            if (ConnectionInfo.ServerSide)
            {
                if (NetworkComms.loggingEnabled) NetworkComms.logger.Debug("New connection detected from " + ConnectionInfo + ", waiting for client connId.");

                //Wait for the client to send its identification
                if (!connectionSetupWait.WaitOne(NetworkComms.connectionEstablishTimeoutMS))
                    throw new ConnectionSetupException("Timeout waiting for client connId with " + ConnectionInfo + ". Connection created at " + ConnectionInfo.ConnectionCreationTime.ToString("HH:mm:ss.fff") + ", its now " + DateTime.Now.ToString("HH:mm:ss.f"));

                if (connectionSetupException)
                {
                    if (NetworkComms.loggingEnabled) NetworkComms.logger.Debug("Connection setup exception. ServerSide. " + connectionSetupExceptionStr);
                    throw new ConnectionSetupException("ServerSide. " + connectionSetupExceptionStr);
                }

                //Once we have the clients id we send our own
                //SendObject(Enum.GetName(typeof(ReservedPacketType), ReservedPacketType.ConnectionSetup), this, false, new ConnectionInfo(NetworkComms.localNetworkIdentifier.ToString(), LocalConnectionIP, NetworkComms.CommsPort), NetworkComms.internalFixedSerializer, NetworkComms.internalFixedCompressor);
                SendObject(Enum.GetName(typeof(ReservedPacketType), ReservedPacketType.ConnectionSetup), new ConnectionInfo(ConnectionType.TCP, NetworkComms.localNetworkIdentifier, new IPEndPoint(ConnectionInfo.LocalEndPoint.Address, NetworkComms.CommsPort)), NetworkComms.internalFixedSendReceiveOptions);
            }
            else
            {
                if (NetworkComms.loggingEnabled) NetworkComms.logger.Debug("Initiating connection to " + ConnectionInfo);

                //As the client we initiated the connection we now forward our local node identifier to the server
                //If we are listening we include our local listen port as well
                //SendObject(Enum.GetName(typeof(ReservedPacketType), ReservedPacketType.ConnectionSetup), this, false, (NetworkComms.isListening ? new ConnectionInfo(NetworkComms.localNetworkIdentifier.ToString(), LocalConnectionIP, NetworkComms.CommsPort) : new ConnectionInfo(NetworkComms.localNetworkIdentifier.ToString(), LocalConnectionIP, -1)), NetworkComms.internalFixedSerializer, NetworkComms.internalFixedCompressor);
                SendObject(Enum.GetName(typeof(ReservedPacketType), ReservedPacketType.ConnectionSetup), new ConnectionInfo(ConnectionType.TCP, NetworkComms.localNetworkIdentifier, new IPEndPoint(ConnectionInfo.LocalEndPoint.Address, (NetworkComms.isListening ? NetworkComms.CommsPort : 0))), NetworkComms.internalFixedSendReceiveOptions);

                //Wait here for the server end to return its own identifier
                if (!connectionSetupWait.WaitOne(NetworkComms.connectionEstablishTimeoutMS))
                    throw new ConnectionSetupException("Timeout waiting for server connId with " + ConnectionInfo + ". Connection created at " + ConnectionInfo.ConnectionCreationTime.ToString("HH:mm:ss.fff") + ", its now " + DateTime.Now.ToString("HH:mm:ss.f"));

                if (connectionSetupException)
                {
                    if (NetworkComms.loggingEnabled) NetworkComms.logger.Debug("Connection setup exception. ClientSide. " + connectionSetupExceptionStr);
                    throw new ConnectionSetupException("ClientSide. " + connectionSetupExceptionStr);
                }
            }

            if (ConnectionInfo.ConnectionShutdown) throw new ConnectionSetupException("Connection was closed during handshake.");

            //A quick idiot test
            if (ConnectionInfo == null) throw new ConnectionSetupException("ConnectionInfo should never be null at this point.");

            if (ConnectionInfo.NetworkIdentifier == null || ConnectionInfo.NetworkIdentifier == ShortGuid.Empty)
                throw new ConnectionSetupException("Remote network identifier should have been set by this point.");

            //Once the connection has been established we may want to re-enable the 'nagle algorithm' used for reducing network congestion (apparently).
            //By default we leave the nagle algorithm disabled because we want the quick through put when sending small packets
            if (NetworkComms.EnableNagleAlgorithmForEstablishedConnections)
            {
                tcpClient.NoDelay = false;
                tcpClient.Client.NoDelay = false;
            }
        }

        /// <summary>
        /// If we were not provided with a tcpClient on creation we need to establish that now
        /// </summary>
        private void ConnectTCPClient()
        {
            try
            {
                if (NetworkComms.loggingEnabled) NetworkComms.logger.Trace("Establishing TCP client with " + ConnectionInfo);

                //We now connect to our target
                tcpClient = new TcpClient();

                bool connectSuccess = true;

                //Start the connection using the asyn version
                IAsyncResult ar = tcpClient.BeginConnect(ConnectionInfo.RemoteEndPoint.Address, ConnectionInfo.RemoteEndPoint.Port, null, null);
                WaitHandle connectionWait = ar.AsyncWaitHandle;
                try
                {
                    if (!ar.AsyncWaitHandle.WaitOne(NetworkComms.connectionEstablishTimeoutMS, false))
                    {
                        tcpClient.Close();
                        connectSuccess = false;
                    }

                    tcpClient.EndConnect(ar);
                }
                finally
                {
                    connectionWait.Close();
                }

                if (!connectSuccess) throw new ConnectionSetupException("Timeout waiting for remoteEndPoint to accept TCP connection.");
            }
            catch (Exception ex)
            {
                CloseConnection(true, 17);
                throw new ConnectionSetupException("Error during TCP connection establish with destination (" + ConnectionInfo + "). Destination may not be listening or connect timed out. " + ex.ToString());
            }
        }

        /// <summary>
        /// Closes a connection
        /// </summary>
        /// <param name="closeDueToError">Closing a connection due an error possibly requires a few extra steps.</param>
        /// <param name="logLocation">Optional debug parameter.</param>
        protected override void CloseConnectionInternal(bool closeDueToError, int logLocation = 0)
        {
            //The following attempts to correctly close the connection
            //Try to close the networkStream first
            try
            {
                if (tcpClientNetworkStream != null) tcpClientNetworkStream.Close();
            }
            catch (Exception)
            {
            }
            finally
            {
                tcpClientNetworkStream = null;
            }

            //Try to close the tcpClient
            try
            {
                tcpClient.Client.Disconnect(false);
                tcpClient.Client.Close();
                tcpClient.Client.Dispose();
            }
            catch (Exception)
            {
            }

            //Try to close the tcpClient
            try
            {
                tcpClient.Close();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Return true if the connection is established within the provided timeout, otherwise false
        /// </summary>
        /// <param name="waitTimeoutMS"></param>
        /// <returns></returns>
        internal bool WaitForConnectionEstablish(int waitTimeoutMS)
        {
            return connectionSetupWait.WaitOne(waitTimeoutMS);
        }

        /// <summary>
        /// Handle an incoming ConnectionSetup packet type
        /// </summary>
        /// <param name="packetDataSection"></param>
        private void ConnectionSetupHandler(byte[] packetDataSection)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Attempts to complete the connection establish with a minimum of locking to prevent possible deadlocking
        /// </summary>
        /// <param name="possibleExistingConnectionWithPeer_ByIdentifier"></param>
        /// <param name="possibleExistingConnectionWithPeer_ByEndPoint"></param>
        /// <returns></returns>
        private bool ConnectionSetupHandlerInner(ref bool possibleExistingConnectionWithPeer_ByIdentifier, ref bool possibleExistingConnectionWithPeer_ByEndPoint, ref Connection existingConnection)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Send the provided packet to the remote peer
        /// </summary>
        /// <param name="packetTypeStr"></param>
        /// <param name="packetData"></param>
        /// <param name="destinationIPAddress"></param>
        /// <param name="receiveConfirmationRequired"></param>
        /// <returns></returns>
        protected override void SendPacket(Packet packet)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Send a null packet (1 byte) to the remotEndPoint. Helps keep the TCP connection alive while ensuring the bandwidth usage is an absolute minimum. If an exception is thrown the connection will be closed.
        /// </summary>
        internal override void SendNullPacket()
        {
            try
            {
                //Only once the connection has been established do we send null packets
                if (ConnectionInfo.ConnectionEstablished)
                {
                    //Multiple threads may try to send packets at the same time so we need this lock to prevent a thread cross talk
                    lock (sendLocker)
                    {
                        //if (NetworkComms.loggingEnabled) NetworkComms.logger.Trace("Sending null packet to " + ConnectionEndPoint.Address + ":" + ConnectionEndPoint.Port + (connectionEstablished ? " (" + ConnectionId + ")." : "."));

                        //Send a single 0 byte
                        tcpClientNetworkStream.Write(new byte[] { 0 }, 0, 1);

                        //Update the traffic time after we have written to netStream
                        ConnectionInfo.UpdateLastTrafficTime();
                    }
                }
            }
            catch (Exception)
            {
                CloseConnection(true, 19);
            }
        }

        public override void SendObject(string sendingPacketType, object objectToSend, SendReceiveOptions options)
        {
            throw new NotImplementedException();
        }

        public override returnObjectType SendReceiveObject<returnObjectType>(string sendingPacketTypeStr, bool receiveConfirmationRequired, string expectedReturnPacketTypeStr, int returnPacketTimeOutMilliSeconds, object sendObject, SendReceiveOptions options)
        {
            throw new NotImplementedException();
        }
    }
}
