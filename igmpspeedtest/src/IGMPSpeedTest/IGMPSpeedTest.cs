using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PacketDotNet;
using SharpPcap;
using SharpPcap.WinPcap;

namespace IGMPSpeedTest
{
    /// <summary>
    /// IgmpSpeedTest - measures IGMP JOIN and LEAVE performance
    /// 
    /// Copyright(C) 2016 NBCUniversal
    /// 
    /// This library is free software; you can redistribute it and/or
    /// modify it under the terms of the GNU Lesser General Public
    /// License as published by the Free Software Foundation; either
    /// version 2.1 of the License, or(at your option) any later version.
    /// 
    /// This library is distributed in the hope that it will be useful,
    /// but WITHOUT ANY WARRANTY; without even the implied warranty of
    /// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU
    /// Lesser General Public License for more details.
    /// 
    /// You should have received a copy of the GNU Lesser General Public
    /// License along with this library; if not, write to the Free Software
    /// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
    /// 
    /// This application operates as a signal generator and a signal analyzer.
    /// One instance emites 1-254 multicast streams, while another instance
    /// issues IGMP JOINs to those streams and measures how long it takes for the network
    /// to begin data delivery on each stream.
    /// 
    /// The receiving application can also measure LEAVE performance, but this currently
    /// is not an accurate measure of network performance (for issues related to WinPcap trigger buffers)
    /// 
    /// Notes:  
    ///  - The emitter and receiver must run on different servers to avoid multicast
    /// loopback problems
    ///  - Measurement accuracy varies across servers and networks, but it reported by
    /// the application.  High resolution timers are used, so sub-milliscond accuracy is
    /// expected in most environments.
    /// 
    /// Developed by intoto systems as a work-for-hire for NBCUniversal, and released
    /// under the LGPL per NBCUniversal request.
    /// </summary>
    class IgmpSpeedTest
    {
        private const int UdpPort = 1234;
        private readonly string _localIp;
        private readonly string _netPrefix;
        private readonly int _firstGroup;
        private readonly int _streamCount;
        private readonly int _timeOutSeconds;
        private readonly bool _quietMode;
        private readonly bool _receiverFlag;
        private readonly bool _includeLeave;
        private readonly short _ttl;
        private readonly DateTime? _startTime;
        private Dictionary<IPAddress, LeaveTimer> _leaveTimers;
        private readonly long _nsecPerTick = (1000L * 1000L * 1000L) / Stopwatch.Frequency;
        IPAddress _localIpAddress;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="localIp">string.  Local IP address on which to listen/transmit multicast traffic</param>
        /// <param name="firstGroup">string.  Class C network to emit multicasts on.  Format="x.x.x"</param>
        /// <param name="streamCount">int.  Listen or Transmit on this many multicast groups (beginning with netprefix.1)</param>
        /// <param name="timeOutSeconds">int.  Dont give up until this many seconds elapses</param>
        /// <param name="quietMode">boolean.  Less verbose output</param>
        /// <param name="receiverFlag">boolean.  True indicates this instance will receive multicasts.  Otherwise we will emite them.</param>
        /// <param name="ttl">short.  Multicast Time-to-Live</param>
        /// <param name="includeLeave">boolean.  indicates whether to perform IGMP LEAVE measurements</param>
        public IgmpSpeedTest(string localIp = "127.0.0.1", string firstGroup = "230.8.97.1", int streamCount = 1, 
                             int timeOutSeconds = 5, bool quietMode = false, bool receiverFlag = false, short ttl = 32, 
                             bool includeLeave = false,DateTime? startTime=null)
        {
            _localIp = localIp;
            _streamCount = streamCount;
            _timeOutSeconds = timeOutSeconds;
            _quietMode = quietMode;
            _receiverFlag = receiverFlag;
            _ttl = ttl;
            _includeLeave = includeLeave;
            _startTime = startTime;

            var match = Regex.Matches(firstGroup, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3})\.(\d{1,3})\b", RegexOptions.None);
            if (match.Count != 1 || match[0].Groups.Count != 3)
                throw new ArgumentException("Could not parse multicast IP group " + firstGroup);
            _netPrefix = match[0].Groups[1].Value;
            _firstGroup = int.Parse(match[0].Groups[2].Value);
            if (!ValidateOptions())
                throw new ArgumentException();
        }

        /// <summary>
        /// Validates command-line options
        /// </summary>
        /// <returns>true if all options are valid, otherwise false</returns>
        bool ValidateOptions()
        {
            // Validate options
            if (_streamCount < 1 || _streamCount > 254)
                throw new ArgumentException("Streamcount must be between 1 and 254");

            if (_streamCount +_firstGroup >254)
                throw new ArgumentException("The stream count must fit within the same class C network space as the multicast group "+_netPrefix);

            if (string.IsNullOrWhiteSpace(_netPrefix))
                throw new ArgumentException("Starting IP cannot be null or empty.");

            try
            {
                _localIpAddress = IPAddress.Parse(_netPrefix);  // note - borrows the localIPAddress variable, which is replaced later.
            }
            catch
            {
                throw new ArgumentException("Could not parse multicast IP group " + _netPrefix);
            }

            if (_localIpAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPV4 addresses allowed.");

            var addressFirstOctet = Convert.ToInt32(_localIpAddress.ToString().Substring(0, _localIpAddress.ToString().IndexOf(".", StringComparison.Ordinal)));

            if (addressFirstOctet < 224 || addressFirstOctet > 239)
                throw new ArgumentException("Network must be in the multicast range of 224.0.0 through 239.255.255");

            try
            {
                _localIpAddress = IPAddress.Parse(_localIp);
            }
            catch
            {
                throw new ArgumentException("Could not parse local IP address  " + _localIp);
            }

            if (_timeOutSeconds < 1)
                throw new ArgumentException("Timeout must be at least 1 second.");

            if (_startTime.HasValue && DateTime.Compare(_startTime.Value, DateTime.Now) <= 0)
                throw new ArgumentException(String.Format("Specified start time {0} is earlier than current time {1}", _startTime.Value, DateTime.Now));

            return true;
        }

        /// <summary>
        /// starts the test process, which will continue until complete or timeout.
        /// </summary>
        public Results BeginTest()
        {
            if (!_quietMode)
            {
                Console.Write("Prepared to " + (_receiverFlag ? "receive " : "emit ") + Convert.ToString(_streamCount) +
                              (_streamCount == 1 ? " stream: " : " streams: "));
                Console.Write(_netPrefix + "."+_firstGroup);
                if (_streamCount > 1)
                {
                    Console.Write(" - " + _netPrefix + "." + (_firstGroup+_streamCount-1));
                }
                Console.WriteLine("");

                if (!_startTime.HasValue)  // do not prompt to start if running in scheduled mode.
                {                    
                    Console.WriteLine("Press ENTER to continue or ^C to abort...");
                    Console.ReadLine();
                }                
            }

            if (_startTime.HasValue)
            {
                if (!_quietMode)
                    Console.WriteLine("Will start automatically in {0} (at {1})", _startTime.Value - DateTime.Now, _startTime.Value);

                if (DateTime.Compare(_startTime.Value, DateTime.Now) > 0)  // if there is still time left to wait, do that now.
                    System.Threading.Thread.Sleep(_startTime.Value - DateTime.Now);
            }

            if (_receiverFlag) return TestStreams();
            EmitStreams();
            return new Results(); // empty results returned in emit mode
        }

        void EmitStreams()
        {
            var packetWatch = new Stopwatch();
            var applicationWatch = new Stopwatch();
            
            // Create and bind the local socket
            var multicastSocket = new Socket(AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            var localEndPoint = new IPEndPoint(_localIpAddress, UdpPort);
            multicastSocket.SendBufferSize = _streamCount; // make a tiny buffer for accurate output measurement
            multicastSocket.Bind(localEndPoint);
            multicastSocket.SetSocketOption(SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive,
                _ttl);

            // create endpoints for each multicast stream
            var multicastEndPoints = new List<IPEndPoint>();

            for (var i = 1; i <= _streamCount; i++)
            {
                var addressString = _netPrefix + "." + (_firstGroup-1+i);
                var multicastIpAddress = IPAddress.Parse(addressString);
                multicastEndPoints.Add(new IPEndPoint(multicastIpAddress, UdpPort));
            }

            var j = 0;
            if (!_quietMode)
            {
                Console.WriteLine("Timing measurements accurate within {0} nanoseconds", _nsecPerTick);
                Console.WriteLine("Emitting.  Press ^C to quit.");
                packetWatch.Start();
            }

            applicationWatch.Start();
            while (applicationWatch.ElapsedMilliseconds < _timeOutSeconds * 1000)
            {
                foreach (var endpoint in multicastEndPoints)
                {
                    multicastSocket.SendTo(Encoding.ASCII.GetBytes("i"), endpoint); // i for intoto
                }
                if (_quietMode) continue;
                //if (j++ % 1000 != 0) continue;
                Console.Write("\rusecs between packets on each stream: " +
                              ((packetWatch.ElapsedTicks / 1000L) * _nsecPerTick) / 1000L + "                 ");
                packetWatch.Restart();
            }
            // Done emitting packets (timeout reached)
            if (!_quietMode)
                Console.WriteLine("\nExiting after timeout period.  Each stream received {0} packets.", j);
        }

        Results TestStreams()
        {
            var applicationWatch = new Stopwatch();
            var bytes = new byte[100];
            var multicastSocket = new Socket(AddressFamily.InterNetwork,
                                             SocketType.Dgram,
                                             ProtocolType.Udp);
            var localEndPoint = new IPEndPoint(_localIpAddress, UdpPort);
            multicastSocket.ReceiveTimeout = 100;  // dont wait more than 100ms for a packet to arrive.
            multicastSocket.Bind(localEndPoint);
            var remoteEndPoint = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            var joinTimers = new Dictionary<IPAddress, Stopwatch>();
            var doneReceivers = 0;
            var pktinfo = new IPPacketInformation();
            var flags = SocketFlags.None;

            // Prep receive environment
            for (var i = 1; i <= _streamCount; i++)
            {
                var addressString = _netPrefix + "." + (_firstGroup-1+i);
                var multicastIpAddress = IPAddress.Parse(addressString);
                joinTimers.Add(multicastIpAddress, new Stopwatch());
            }

            // Issue JOIN commands and start stopwatches.
            foreach (var r in joinTimers)
            {
                multicastSocket.SetSocketOption(SocketOptionLevel.IP,
                                                SocketOptionName.AddMembership,
                                                new MulticastOption(r.Key, _localIpAddress));
                r.Value.Start();
            }

            applicationWatch.Start();
            // gather response times for all streams
            while (doneReceivers < _streamCount && applicationWatch.ElapsedMilliseconds < _timeOutSeconds * 1000)
            {
                var timedout = false;
                try
                {
                    multicastSocket.ReceiveMessageFrom(bytes, 0, 1, ref flags, ref remoteEndPoint, out pktinfo);
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode.ToString().Equals("TimedOut"))
                        timedout = true;  // perfectly normal, proceed 
                    else
                        throw;
                }

                if (!timedout && (flags & SocketFlags.Multicast) != 0)
                {
                    if (joinTimers.ContainsKey(pktinfo.Address) && joinTimers[pktinfo.Address].IsRunning)
                    {
                        joinTimers[pktinfo.Address].Stop();
                        doneReceivers++;
                    }
                }

                flags = SocketFlags.None;
            }
            // calculate JOIN results
            var results = new Results
            {
                HasResults = joinTimers.Any(c => !c.Value.IsRunning)
            };


            foreach (var r in joinTimers.Where(v => !v.Value.IsRunning))
                results.JoinTime.Add(r.Key, r.Value.ElapsedTicks * _nsecPerTick / 1000L);

            if (results.HasResults) // did we get packets for any groups?
            {
                results.FastestJoin = (float)joinTimers.Where(c => !c.Value.IsRunning).Min(c => c.Value.ElapsedTicks) * _nsecPerTick / 1000L;
                results.AverageJoin = (float)joinTimers.Where(c => !c.Value.IsRunning).Average(c => c.Value.ElapsedTicks) * _nsecPerTick / 1000L;
                results.SlowestJoin = (float)joinTimers.Where(c => !c.Value.IsRunning).Max(c => c.Value.ElapsedTicks) * _nsecPerTick / 1000L;
            }


            if (applicationWatch.ElapsedMilliseconds >= _timeOutSeconds * 1000 || !_includeLeave)
            {
                results.IsComplete = (joinTimers.Count(c => !c.Value.IsRunning) == _streamCount);
                return results;  // do not proceed to LEAVE measurement
            }
                

            // LEAVE processing must use promiscuous mode.  If we do this using sockets
            // Windows will interfere in multiple ways (respond to membership queries,
            // stop delivering inbound packets to the socket, etc).

            var nic = GetIncomingNic();
            nic.OnPacketArrival += ReceivePacket;

            nic.Open(DeviceMode.Promiscuous);

            // Reset timers and perform LEAVE measurement
            _leaveTimers = new Dictionary<IPAddress, LeaveTimer>();
            for (var i = 1; i <= _streamCount; i++)
            {
                var addressString = _netPrefix + "." + (_firstGroup+i-1);
                var multicastIpAddress = IPAddress.Parse(addressString);
                _leaveTimers.Add(multicastIpAddress, new LeaveTimer());
            }

            //build a filter string for the promiscuous capture
            var filter="";
            foreach (var timer in _leaveTimers)
            {
                filter += "dst host " + timer.Key;
                if (!timer.Equals(_leaveTimers.Last())) filter += " or ";
            }
            
            nic.Filter = filter;
                
            
            // Issue LEAVE commands and start stopwatch.
                foreach (var r in _leaveTimers)
            {
                multicastSocket.SetSocketOption(SocketOptionLevel.IP,
                    SocketOptionName.DropMembership,
                    new MulticastOption(r.Key, _localIpAddress));
                r.Value.Stopwatch.Start();
            }

            nic.StartCapture();

            doneReceivers = 0;

            // Spin until we either timeout or no group has received a packet in the wait period
            // note:  _leavetimers is being updated in the ReceivePacket event
            while (doneReceivers < _streamCount && applicationWatch.ElapsedMilliseconds < _timeOutSeconds * 1000)
            {
                doneReceivers = _leaveTimers.Count(c => c.Value.MicroSecondsWithoutPacket() >= 2000000);
            }

            nic.StopCapture();

            foreach (var r in _leaveTimers)
                results.LeaveTime.Add(r.Key, r.Value.LastPacketSeen * _nsecPerTick / 1000L);

            if (results.LeaveTime.Any(c => c.Value != 0)) // did we get packets for any groups?
            {
                results.FastestLeave = results.LeaveTime.Where(c => c.Value != 0).Min(c => c.Value);
                results.AverageLeave = (float)results.LeaveTime.Where(c => c.Value != 0).Average(c => c.Value);
                results.SlowestLeave = results.LeaveTime.Where(c => c.Value != 0).Max(c => c.Value);
            }

            results.IsComplete = (joinTimers.Count(c => !c.Value.IsRunning) == _streamCount &&
                _leaveTimers.Count(c => c.Value.LastPacketSeen != 0) == _streamCount);

            return results;
        }

        private void ReceivePacket(object sender, CaptureEventArgs e)
        {
            var packet = PacketDotNet.Packet.ParsePacket(e.Packet.LinkLayerType, e.Packet.Data);
            var ipPacket = (IpPacket) packet.Extract(typeof (IpPacket));
            if (_leaveTimers.ContainsKey(ipPacket.DestinationAddress))
                _leaveTimers[ipPacket.DestinationAddress].PacketSeen();
        }

        /// <summary>
        /// Finds all the NICs in the machine
        /// </summary>
        /// <returns></returns>
        private static WinPcapDeviceList GetNics()
        {
            var allNics = WinPcapDeviceList.Instance;
            if (allNics.Count == 0)
                throw (new NullReferenceException("Cannot find any local interfaces.  Make sure WinPcap is installed."));
            return allNics;
        }

        /// <summary>
        /// Given a list of NICs, finds the one with the IP address specified in LocalIPAddress
        /// </summary>
        /// <param name="nics"></param>
        /// <returns></returns>
        private WinPcapDevice GetIncomingNic()
        {
            foreach (var nic in GetNics().Where(nic => nic.Interface.Addresses.Any(address => address.ToString().Contains(_localIp))))
                return nic;
            throw (new NullReferenceException("Cannot find local address " + _localIp + " on any NIC"));
        }
    }


    class LeaveTimer
    {
        public Stopwatch Stopwatch = new Stopwatch();
        public long LastPacketSeen;

        public void PacketSeen()
        {
            LastPacketSeen = Stopwatch.ElapsedTicks;
        }

        public long MicroSecondsWithoutPacket()
        {
            var nsecPerTick = (1000L * 1000L * 1000L) / Stopwatch.Frequency;
            var j = ((Stopwatch.ElapsedTicks - LastPacketSeen) * nsecPerTick) / 1000L;
            return j;
        }

    }
}


